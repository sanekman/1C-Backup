using System.Net;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class SqlAuthenticationTests
{
    private static BackupJob Job => new()
    {
        Name = "SQL credentials fixture", Kind = DatabaseKind.SqlServer, Source = "fixture-db",
        DestinationDirectory = Path.GetTempPath(),
        SqlServer = new() { Server = "sql.example", ServerBackupDirectory = "/stage", AgentReadDirectory = Path.GetTempPath() }
    };

    [Fact]
    public async Task LegacySettingsKeepWindowsAuthenticationAndNeverReadSqlPasswords()
    {
        var settings = JsonSerializer.Deserialize<SqlServerBackupSettings>(JsonSerializer.Serialize(Job.SqlServer))!;
        Assert.Equal(SqlServerAuthentication.Windows, settings.Authentication);
        Assert.DoesNotContain("Authentication", JsonSerializer.Serialize(settings));
        var credentials = new Credentials();
        await using var connection = (SqlConnection)await SqlServerBackupExecutor.CreateConnectionAsync(Job, credentials);
        Assert.True(new SqlConnectionStringBuilder(connection.ConnectionString).IntegratedSecurity);
        Assert.Null(connection.Credential);
        Assert.Equal(0, credentials.Reads);
    }

    [Fact]
    public async Task SqlLoginUsesSeparateCredentialAndKeepsPasswordOutOfConnectionString()
    {
        var job = Job with { SqlServer = Job.SqlServer! with { Authentication = SqlServerAuthentication.SqlServer, CredentialKey = "sql-fixture-key" } };
        var credentials = new Credentials();
        await using var connection = (SqlConnection)await SqlServerBackupExecutor.CreateConnectionAsync(job, credentials);
        var builder = new SqlConnectionStringBuilder(connection.ConnectionString);
        Assert.False(builder.IntegratedSecurity);
        Assert.False(builder.TrustServerCertificate);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, builder.Encrypt);
        Assert.False(builder.Pooling); // One new SqlCredential per operation must not leave a pool per credential instance.
        Assert.Equal("backup-fixture-login", connection.Credential!.UserId);
        Assert.True(connection.Credential.Password.IsReadOnly());
        Assert.Equal("sql-fixture-key", credentials.LastKey);
        Assert.Equal(1, credentials.Reads);
        Assert.DoesNotContain(Credentials.Password, connection.ConnectionString);
        Assert.DoesNotContain(Credentials.Password, JsonSerializer.Serialize(job));
        Assert.DoesNotContain("backup-fixture-login", JsonSerializer.Serialize(job));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("key\nvalue")]
    public async Task InvalidCredentialBindingFailsBeforeCredentialRead(string? key)
    {
        var credentials = new Credentials();
        var job = Job with { SqlServer = Job.SqlServer! with { Authentication = SqlServerAuthentication.SqlServer, CredentialKey = key } };
        await Assert.ThrowsAsync<InvalidDataException>(() => SqlServerBackupExecutor.CreateConnectionAsync(job, credentials));
        Assert.Equal(0, credentials.Reads);
    }

    [Fact]
    public async Task CancelledReadNeverLoadsCredentials()
    {
        var credentials = new Credentials();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SqlServerBackupExecutor.CreateConnectionAsync(Job, credentials, cancellation.Token));
        Assert.Equal(0, credentials.Reads);
    }

    [Fact]
    public void UnknownAuthenticationAndOverlongKeyAreRejected()
    {
        Assert.NotEmpty((Job.SqlServer! with { Authentication = (SqlServerAuthentication)99 }).Validate());
        Assert.NotEmpty((Job.SqlServer! with { Authentication = SqlServerAuthentication.SqlServer, CredentialKey = new string('k', 513) }).Validate());
    }

    [Fact]
    public async Task JobStoreAndTransferPreserveBindingWithoutPasswords()
    {
        var root = Path.Combine(Path.GetTempPath(), "OneCBackup-sql-auth-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            var job = Job with { SqlServer = Job.SqlServer! with { Authentication = SqlServerAuthentication.SqlServer, CredentialKey = "separate-sql-key" } };
            var path = Path.Combine(root, "export.json");
            await BackupJobTransfer.ExportAsync(job, path);
            var imported = await BackupJobTransfer.ImportAsync(path);
            Assert.Equal(job.SqlServer, imported.SqlServer);
            var store = new JobStore(Path.Combine(root, "jobs.json"));
            await store.SaveAsync([job]);
            Assert.Equal(job.SqlServer, Assert.Single(await store.LoadAsync()).SqlServer);
            var json = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("Password", json);
            // Settings documents accept references, never inline SQL passwords.
            var text = json.Replace("\"Server\":", "\"Password\": \"must-not-be-accepted\", \"Server\":");
            Assert.NotEqual(json, text);
            await File.WriteAllTextAsync(path, text);
            await Assert.ThrowsAsync<JsonException>(() => BackupJobTransfer.ImportAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Credentials : ISqlServerCredentialStore
    {
        public const string Password = "test-password-not-for-json";
        public int Reads { get; private set; }
        public string? LastKey { get; private set; }
        public Task<NetworkCredential> GetAsync(string key, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Reads++; LastKey = key;
            return Task.FromResult(new NetworkCredential("backup-fixture-login", Password));
        }
    }
}
