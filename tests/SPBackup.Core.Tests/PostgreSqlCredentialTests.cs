using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class PostgreSqlCredentialTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "onec-pg-credentials-test-" + Guid.NewGuid().ToString("N"));
    private const string Password = "пароль:with\\escapes";
    private sealed class Credentials(string username = "backup-user", string password = Password) : IPostgreSqlCredentialStore
    {
        public Task<NetworkCredential> GetAsync(string key, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Assert.Equal("saved-pg", key); return Task.FromResult(new NetworkCredential(username, password)); }
    }
    private sealed class Process(Func<BackupProcess, CancellationToken, Task<int>> run) : IBackupProcessRunner
    { public Task<int> RunAsync(BackupProcess command, CancellationToken token) => run(command, token); }
    public PostgreSqlCredentialTests()
    {
        Directory.CreateDirectory(root);
        foreach (var name in new[] { "pg_dump", "pg_restore", "psql" })
            File.WriteAllText(Path.Combine(root, name + (OperatingSystem.IsWindows() ? ".exe" : "")), "fixture, never execute");
    }
    private BackupJob Job => new() { Kind = DatabaseKind.PostgreSql, Name = "PG credentials", Source = "database:with\\escapes",
        DestinationDirectory = Path.Combine(root, "copies"), TemporaryDirectory = Path.Combine(root, "work"), ScheduleKind = ScheduleKind.Manual,
        PostgreSql = new() { Host = "host:with\\escapes", Port = 5433, Username = "backup-user", CredentialKey = "saved-pg",
            PgDumpPath = Path.Combine(root, "pg_dump" + (OperatingSystem.IsWindows() ? ".exe" : "")),
            PgRestorePath = Path.Combine(root, "pg_restore" + (OperatingSystem.IsWindows() ? ".exe" : "")) } };

    private async Task<string> Inspect(BackupProcess command)
    {
        var env = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string?>>(command.Environment);
        var path = Assert.IsType<string>(env["PGPASSFILE"]);
        Assert.DoesNotContain(Password, string.Join(" ", command.Arguments));
        foreach (var key in new[] { "PGPASSWORD", "PGSERVICE", "PGSERVICEFILE", "PGHOSTADDR" }) Assert.Null(env[key]);
        Assert.False(path.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase));
        Assert.Equal("host\\:with\\\\escapes:5433:database\\:with\\\\escapes:backup-user:пароль\\:with\\\\escapes\n", await File.ReadAllTextAsync(path));
        if (OperatingSystem.IsWindows())
        {
            var allowed = new[] { WindowsIdentity.GetCurrent().User!.Value, "S-1-5-18", "S-1-5-32-544" };
            var acl = new DirectoryInfo(Path.GetDirectoryName(path)!).GetAccessControl();
            Assert.True(acl.AreAccessRulesProtected);
            foreach (var rule in new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>())
                Assert.Contains(rule.IdentityReference.Value, allowed);
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(path)!));
        }
        return path;
    }

    [Theory]
    [InlineData("success")]
    [InlineData("dump-error")]
    [InlineData("catalog-error")]
    [InlineData("cancel")]
    [InlineData("start-error")]
    public async Task BackupUsesPrivateScopedFileAndRemovesItAfterSuccessFailureOrCancellation(string outcome)
    {
        string? path = null; var calls = 0;
        using var cancellation = new CancellationTokenSource();
        var process = new Process(async (command, token) =>
        {
            path = await Inspect(command); calls++;
            if (outcome == "start-error") throw new IOException("fixture start error");
            if (outcome == "cancel") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            if (calls == 1)
            {
                if (outcome == "dump-error") return 1;
                await File.WriteAllTextAsync(command.Arguments.Single(arg => arg.StartsWith("--file="))[7..], "dump fixture", token);
            }
            return calls == 2 && outcome == "catalog-error" ? 2 : 0;
        });
        var action = new PostgreSqlBackupExecutor(process, new Credentials()).CreateAsync(Job, Path.Combine(root, "work"), cancellation.Token);
        if (outcome == "success") Assert.True(File.Exists(await action));
        else if (outcome == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action);
        else if (outcome == "catalog-error") await Assert.ThrowsAsync<InvalidDataException>(() => action);
        else await Assert.ThrowsAnyAsync<IOException>(() => action);
        Assert.NotNull(path); Assert.False(File.Exists(path)); Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        Assert.Equal(outcome is "success" or "catalog-error" ? 2 : 1, calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SourceCheckUsesSameScopedFileAndCleansUpForAllResults(int outcome)
    {
        string? path = null;
        using var cancellation = new CancellationTokenSource();
        var process = new Process(async (command, token) =>
        {
            path = await Inspect(command);
            if (outcome == 2) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            return outcome;
        });
        var report = await new SourceReadinessChecker(process, root, postgreSqlCredentials: new Credentials()).CheckAsync(Job, cancellation.Token);
        Assert.Equal(outcome == 0, report.Succeeded); Assert.Equal(outcome == 2, report.Cancelled);
        Assert.NotNull(path); Assert.False(File.Exists(path)); Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Theory]
    [InlineData("other-user", "valid")]
    [InlineData("backup-user", "bad\npassword")]
    [InlineData("backup-user", "bad\0password")]
    public async Task InvalidCredentialCannotStartAProcess(string username, string password)
    {
        var process = new Process((_, _) => throw new InvalidOperationException("must not launch"));
        await Assert.ThrowsAsync<InvalidDataException>(() => new PostgreSqlBackupExecutor(process, new Credentials(username, password))
            .CreateAsync(Job, Path.Combine(root, "work"), CancellationToken.None));
    }

    [Fact]
    public async Task LegacyJobKeepsLibpqEnvironmentAndOmitsNewFieldFromJson()
    {
        var legacy = Job with { PostgreSql = Job.PostgreSql! with { CredentialKey = null } };
        var json = JsonSerializer.Serialize(legacy); Assert.DoesNotContain("CredentialKey", json);
        Assert.Null(JsonSerializer.Deserialize<BackupJob>(json)!.PostgreSql!.CredentialKey);
        using var environment = await PostgreSqlConnectionEnvironment.OpenAsync(legacy, null, CancellationToken.None);
        Assert.Null(environment.Environment);
    }

    [Theory]
    [InlineData("host", "*")]
    [InlineData("host", "first,second")]
    [InlineData("host", "host\nother")]
    [InlineData("host", null)]
    [InlineData("username", "*")]
    [InlineData("username", null)]
    [InlineData("database", "*")]
    [InlineData("database", "database\nother")]
    public void SavedCredentialsCannotBeBoundToWildcardOrInjectedPasswordFileFields(string field, string? value)
    {
        var job = Job;
        job = field switch
        {
            "host" => job with { PostgreSql = job.PostgreSql! with { Host = value! } },
            "username" => job with { PostgreSql = job.PostgreSql! with { Username = value! } },
            _ => job with { Source = value! }
        };
        Assert.NotEmpty(job.Validate());
    }

    [Fact]
    public async Task RealProcessEnvironmentOverridesOnlyChildAndCanRemoveInheritedVariables()
    {
        var key = "ONECBACKUP_PG_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(key, "parent-value");
        try
        {
            var command = OperatingSystem.IsWindows()
                ? new BackupProcess(Environment.GetEnvironmentVariable("COMSPEC")!, ["/c", "if defined " + key + " (exit /b 9) else (exit /b 0)"], root)
                : new BackupProcess("/bin/sh", ["-c", "test -z \"$(printenv " + key + ")\""], root);
            Assert.Equal(0, await new BackupProcessRunner().RunAsync(command with { Environment = new Dictionary<string, string?> { [key] = null } }, CancellationToken.None));
            Assert.Equal("parent-value", Environment.GetEnvironmentVariable(key));
        }
        finally { Environment.SetEnvironmentVariable(key, null); }
    }
    public void Dispose() => Directory.Delete(root, true);
}
