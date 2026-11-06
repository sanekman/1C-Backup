using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class SourceReadinessCheckerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "onec-source-check-" + Guid.NewGuid().ToString("N"));
    private sealed class Process(Func<BackupProcess, CancellationToken, Task<int>> execute) : IBackupProcessRunner
    {
        public List<BackupProcess> Calls { get; } = [];
        public Task<int> RunAsync(BackupProcess command, CancellationToken token)
        { Calls.Add(command); return execute(command, token); }
    }
    public SourceReadinessCheckerTests() => Directory.CreateDirectory(root);
    private BackupJob FileJob => new() { Name = "source", Source = root, Kind = DatabaseKind.OneCFile,
        OneCMethod = OneCBackupMethod.FileArchive, DestinationDirectory = Path.Combine(root, "copies"), ScheduleKind = ScheduleKind.Manual };
    private string Database => Path.Combine(root, "1Cv8.1CD");
    private string Work => Path.Combine(root, "work");
    private SourceReadinessChecker Checker(Process process) => new(process, Work);
    private BackupJob PgJob()
    {
        var dump = Path.Combine(root, "pg_dump.exe"); var restore = Path.Combine(root, "pg_restore.exe");
        File.WriteAllText(dump, "fixture"); File.WriteAllText(restore, "fixture");
        return FileJob with { Kind = DatabaseKind.PostgreSql, Source = "database with spaces",
            PostgreSql = new() { PgDumpPath = dump, PgRestorePath = restore, Host = "pg.example.com", Port = 5433, Username = "backup-user" } };
    }
    private void Psql() => File.WriteAllText(Path.Combine(root, OperatingSystem.IsWindows() ? "psql.exe" : "psql"), "fixture");
    private static Process NoProcess() => new((_, _) => throw new InvalidOperationException("A file check must not start any process"));

    [Fact]
    public async Task ExclusiveFileCheckLeavesBytesUnchangedReleasesHandleAndCreatesNoWork()
    {
        var bytes = new byte[] { 1, 8, 4, 2 }; await File.WriteAllBytesAsync(Database, bytes);
        var process = NoProcess(); var result = await Checker(process).CheckAsync(FileJob);
        Assert.True(result.Succeeded, result.Details); Assert.Empty(process.Calls);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Database)); Assert.False(Directory.Exists(Work));
        using var exclusive = new FileStream(Database, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.False(Directory.Exists(FileJob.DestinationDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrEmptyFileCannotBeReportedReady(bool empty)
    {
        if (empty) await File.WriteAllBytesAsync(Database, []);
        Assert.False((await Checker(NoProcess()).CheckAsync(FileJob)).Succeeded);
        Assert.False(Directory.Exists(Work)); Assert.False(Directory.Exists(FileJob.DestinationDirectory));
    }

    [Fact]
    public async Task BusyDatabaseFailsWithoutChangingItOrForcingSessions()
    {
        await File.WriteAllTextAsync(Database, "fixture");
        await using var held = new FileStream(Database, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await Checker(NoProcess()).CheckAsync(FileJob);
        Assert.False(result.Succeeded); Assert.Contains("Закройте сеансы", result.Details); Assert.Equal(7, held.Length);
    }

    [Fact]
    public async Task SharedFileCheckAllowsWritableSessionAndExplainsConsistencyRisk()
    {
        await File.WriteAllTextAsync(Database, "fixture");
        await using var held = new FileStream(Database, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var result = await Checker(NoProcess()).CheckAsync(FileJob with { AllowOpenOneCDatabase = true });
        Assert.True(result.Succeeded, result.Details);
        if (OperatingSystem.IsWindows()) Assert.Contains(OneCFileArchiveExecutor.OpenDatabaseWarning, result.Details);
        Assert.False(Directory.Exists(Work));
    }

    [Fact]
    public async Task ClosedDatabaseCheckWithOptInDoesNotReportSharedAccessWarning()
    {
        await File.WriteAllTextAsync(Database, "fixture");
        var result = await Checker(NoProcess()).CheckAsync(FileJob with { AllowOpenOneCDatabase = true });
        Assert.True(result.Succeeded, result.Details);
        Assert.DoesNotContain(OneCFileArchiveExecutor.OpenDatabaseWarning, result.Details);
        Assert.Contains("эксклюзивный доступ получен", result.Details);
        using var released = new FileStream(Database, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task PlatformCheckRequiresExecutableButDoesNotRunIt()
    {
        await File.WriteAllTextAsync(Database, "fixture");
        var executable = Path.Combine(root, "1cv8.exe");
        var job = FileJob with { OneCMethod = OneCBackupMethod.PlatformDump, OneC = new() { ExecutablePath = executable } };
        var process = NoProcess(); Assert.False((await Checker(process).CheckAsync(job)).Succeeded);
        await File.WriteAllTextAsync(executable, "fixture executable");
        var ready = await Checker(process).CheckAsync(job); Assert.True(ready.Succeeded, ready.Details);
        Assert.Contains("Платформа не запускалась", ready.Details); Assert.Empty(process.Calls);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(5, false)]
    public async Task PostgreSqlRunsOnlyReadOnlyQueryAndCleansPrivateProbe(int exitCode, bool success)
    {
        var job = PgJob(); Psql();
        var unrelated = Path.Combine(root, "work", "customer.txt"); Directory.CreateDirectory(Work); await File.WriteAllTextAsync(unrelated, "keep");
        var process = new Process(async (command, token) =>
        {
            await File.WriteAllTextAsync(command.ErrorLogPath!, "fixture", token); return exitCode;
        });
        var result = await Checker(process).CheckAsync(job); Assert.Equal(success, result.Succeeded);
        if (!success)
        {
            Assert.Contains("Код выхода не определяет точную причину", result.Details);
            Assert.Contains("редакторе задания", result.Details);
            Assert.DoesNotContain("fixture", result.Details);
        }
        var call = Assert.Single(process.Calls);
        Assert.Contains("--dbname=database with spaces", call.Arguments);
        Assert.Contains("--command=SELECT 1;", call.Arguments); Assert.Contains("--no-password", call.Arguments);
        Assert.Contains("--host=pg.example.com", call.Arguments); Assert.Contains("--port=5433", call.Arguments);
        Assert.True(call.DiscardStandardOutput); Assert.StartsWith(Work + Path.DirectorySeparatorChar, call.WorkingDirectory);
        Assert.False(Directory.Exists(call.WorkingDirectory)); Assert.Single(Directory.GetFileSystemEntries(Work));
        Assert.Equal("keep", await File.ReadAllTextAsync(unrelated)); Assert.False(Directory.Exists(job.DestinationDirectory));
    }

    [Fact]
    public async Task PostgreSqlWithoutPsqlExplainsOptionalToolWithoutStartingDump()
    {
        var job = PgJob(); var process = NoProcess();
        var result = await Checker(process).CheckAsync(job); Assert.False(result.Succeeded); Assert.Contains("psql", result.Details);
        Assert.Empty(process.Calls); Assert.False(Directory.Exists(Work));
    }

    [Fact]
    public async Task CancelledPostgreSqlProbeRemovesOnlyOwnFiles()
    {
        var job = PgJob(); Psql(); using var cancellation = new CancellationTokenSource();
        var process = new Process((_, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(0); });
        var result = await Checker(process).CheckAsync(job, cancellation.Token);
        Assert.True(result.Cancelled); Assert.False(result.Succeeded); Assert.Empty(Directory.GetFileSystemEntries(Work));
    }

    [Fact]
    public async Task PreCancelledCheckDoesNotTouchSourceOrStartProcesses()
    {
        await File.WriteAllTextAsync(Database, "fixture"); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var process = NoProcess(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Checker(process).CheckAsync(FileJob, cancellation.Token));
        Assert.Empty(process.Calls); Assert.Equal("fixture", await File.ReadAllTextAsync(Database)); Assert.False(Directory.Exists(Work));
    }
    public void Dispose() => Directory.Delete(root, recursive: true);
}
