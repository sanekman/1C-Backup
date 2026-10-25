using System.Net;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class SqlMonitorAuthenticationTests
{
    private static ExternalBackupMonitor Monitor => new() { Name = "SQL history", SqlServer = "sql.example" };

    [Fact]
    public async Task OldMonitorKeepsWindowsAuthenticationWithoutLoadingSecrets()
    {
        var monitor = JsonSerializer.Deserialize<ExternalBackupMonitor>("""{"Name":"Legacy","SqlServer":"sql.example"}""")!;
        var credentials = new Credentials();
        await using var connection = (SqlConnection)await SqlServerBackupHistorySource.CreateConnectionAsync(monitor, credentials);
        var builder = new SqlConnectionStringBuilder(connection.ConnectionString);
        Assert.True(builder.IntegratedSecurity); Assert.Equal("msdb", builder.InitialCatalog);
        Assert.False(builder.TrustServerCertificate); Assert.Equal(SqlConnectionEncryptOption.Mandatory, builder.Encrypt);
        Assert.Null(connection.Credential); Assert.Equal(0, credentials.Reads);
        Assert.DoesNotContain("SqlAuthentication", JsonSerializer.Serialize(monitor));
        Assert.DoesNotContain("SqlCredentialKey", JsonSerializer.Serialize(monitor));
    }

    [Fact]
    public async Task MonitorUsesSeparateSqlCredentialWithSameTlsPolicyAsBackups()
    {
        var credentials = new Credentials();
        var monitor = Monitor with { SqlAuthentication = SqlServerAuthentication.SqlServer, SqlCredentialKey = "monitor-only-key" };
        await using var connection = (SqlConnection)await SqlServerBackupHistorySource.CreateConnectionAsync(monitor, credentials);
        var builder = new SqlConnectionStringBuilder(connection.ConnectionString);
        Assert.False(builder.IntegratedSecurity); Assert.False(builder.TrustServerCertificate);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, builder.Encrypt); Assert.False(builder.Pooling);
        Assert.Equal("msdb", builder.InitialCatalog); Assert.Equal("SP Backup monitor", builder.ApplicationName);
        Assert.Equal("history-reader", connection.Credential!.UserId); Assert.True(connection.Credential.Password.IsReadOnly());
        Assert.Equal("monitor-only-key", credentials.LastKey); Assert.Equal(1, credentials.Reads);
        Assert.DoesNotContain(Credentials.Secret, connection.ConnectionString);
        Assert.DoesNotContain("history-reader", connection.ConnectionString);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")] [InlineData("key\nother")]
    public async Task InvalidBindingIsRejectedBeforeReadingPassword(string? key)
    {
        var credentials = new Credentials();
        await Assert.ThrowsAsync<InvalidDataException>(() => SqlServerBackupHistorySource.CreateConnectionAsync(
            Monitor with { SqlAuthentication = SqlServerAuthentication.SqlServer, SqlCredentialKey = key }, credentials));
        Assert.Equal(0, credentials.Reads);
    }

    [Fact]
    public async Task CancelledReadAndInvalidModeDoNotLoadPassword()
    {
        var credentials = new Credentials();
        var monitor = Monitor with { SqlAuthentication = SqlServerAuthentication.SqlServer, SqlCredentialKey = "key" };
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SqlServerBackupHistorySource.CreateConnectionAsync(monitor, credentials, cancelled.Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => SqlServerBackupHistorySource.CreateConnectionAsync(monitor with { SqlAuthentication = (SqlServerAuthentication)99 }, credentials));
        Assert.Throws<InvalidDataException>(() => (monitor with { SqlCredentialKey = new string('k', 513) }).Validate());
        Assert.Equal(0, credentials.Reads);
    }

    [Fact]
    public async Task SavedMonitorRoundTripsOnlyReferenceAndRejectsInvalidReplacement()
    {
        var root = Path.Combine(Path.GetTempPath(), "OneCBackup-monitor-auth-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "external-monitors.json"); var store = new ExternalBackupMonitorStore(path);
            var monitor = Monitor with { SqlAuthentication = SqlServerAuthentication.SqlServer, SqlCredentialKey = "monitor-key" };
            await store.SaveAsync([monitor]); Assert.Equal(monitor, Assert.Single(await store.LoadAsync()));
            var before = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain(Credentials.Secret, before); Assert.DoesNotContain("history-reader", before);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync([monitor with { SqlCredentialKey = null }]));
            Assert.Equal(before, await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Credentials : ISqlServerCredentialStore
    {
        internal const string Secret = "fixture-password-never-in-json";
        public int Reads { get; private set; }
        public string? LastKey { get; private set; }
        public Task<NetworkCredential> GetAsync(string key, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Reads++; LastKey = key; return Task.FromResult(new NetworkCredential("history-reader", Secret)); }
    }
}
