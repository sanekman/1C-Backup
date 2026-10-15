using System.IO.Compression;
using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class LocalExecutionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OneCBackup-execution-" + Guid.NewGuid());
    private readonly Guid jobId = Guid.NewGuid();
    private BackupJob Job => new()
    {
        Id = jobId, Name = "File database", Source = Path.Combine(root, "database with spaces"),
        DestinationDirectory = Path.Combine(root, "copies"),
        OneC = new OneCSettings { ExecutablePath = Path.Combine(root, "platform with spaces.exe") }
    };

    public LocalExecutionTests()
    {
        Directory.CreateDirectory(Job.Source);
        File.WriteAllText(Path.Combine(Job.Source, "1Cv8.1CD"), "source database");
        File.WriteAllText(Job.OneC!.ExecutablePath, "test platform marker");
    }

    private BackupRunHistory History => new(Path.Combine(root, "runs.json"));

    private LocalBackupRunner Runner(IBackupProcessRunner process, BackupRunHistory history) => new(
        new OneCBackupExecutor(process),
        new BackupCompletion(new FileBackupStorage(), new BackupCatalog(Path.Combine(root, "catalog.json"))),
        history, Path.Combine(root, "work"));

    [Fact]
    public async Task DumpIsDeliveredArchivedRegisteredAndRecordedBeforeWorkingFilesAreRemoved()
    {
        var history = History;
        var process = new FakeProcess(async (command, ct) =>
        {
            Assert.Equal(BackupRunStatus.Running, Assert.Single(await history.ReadAsync(ct)).Status);
            Assert.Equal(Job.OneC!.ExecutablePath, command.Executable);
            Assert.Equal("DESIGNER", command.Arguments[0]);
            Assert.Equal(Job.Source, command.Arguments[2]);
            Assert.Contains("/DisableStartupDialogs", command.Arguments);
            await File.WriteAllTextAsync(command.Arguments[4], "valid dump fixture", ct);
            return 0;
        });
        var result = await Runner(process, history).RunAsync(Job with { ArchiveCopies = true });
        Assert.Equal(BackupRunStatus.Succeeded, result.Status);
        Assert.NotNull(result.FinishedUtc);
        Assert.False(Directory.Exists(result.WorkDirectory));
        Assert.NotNull(result.Backup);
        using var zip = ZipFile.OpenRead(result.Backup.Location);
        using var reader = new StreamReader(Assert.Single(zip.Entries).Open());
        Assert.Equal("valid dump fixture", await reader.ReadToEndAsync());
        Assert.Single(await new BackupCatalog(Path.Combine(root, "catalog.json")).ReadAsync());
        Assert.Equal(BackupRunStatus.Succeeded, Assert.Single(await History.ReadAsync()).Status);
        Assert.Equal("source database", await File.ReadAllTextAsync(Path.Combine(Job.Source, "1Cv8.1CD")));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public async Task PlatformFailureOrMissingDumpPreservesExistingCopyAndWorkingDirectory(int code, bool writeDump)
    {
        var history = History;
        var good = new FakeProcess(async (command, ct) =>
        { await File.WriteAllTextAsync(command.Arguments[4], "good", ct); return 0; });
        var previous = await Runner(good, history).RunAsync(Job with { KeepLast = 1 });
        var bad = new FakeProcess(async (command, ct) =>
        {
            if (writeDump) await File.WriteAllTextAsync(command.Arguments[4], "partial", ct);
            return code;
        });
        var result = await Runner(bad, history).RunAsync(Job with { KeepLast = 1 });
        Assert.Equal(BackupRunStatus.Failed, result.Status);
        Assert.Contains("Журнал:", result.Error);
        Assert.True(Directory.Exists(result.WorkDirectory));
        Assert.True(File.Exists(previous.Backup!.Location));
        Assert.Single(await new BackupCatalog(Path.Combine(root, "catalog.json")).ReadAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedTemporaryFolderContainsDumpAndArchiveAndPreservesUnrelatedFiles(bool nativeArchive)
    {
        var temporary = Path.Combine(root, "HDD temporary");
        Directory.CreateDirectory(temporary);
        var marker = Path.Combine(temporary, "keep-me.txt");
        await File.WriteAllTextAsync(marker, "unrelated file");
        var storage = new TrackingStorage();
        var process = new FakeProcess(async (command, ct) =>
        {
            Assert.StartsWith(temporary + Path.DirectorySeparatorChar, command.Arguments[4]);
            await File.WriteAllTextAsync(command.Arguments[4], "dump fixture", ct);
            return 0;
        });
        IBackupExecutor executor = nativeArchive ? new OneCFileArchiveExecutor() : new OneCBackupExecutor(process);
        var job = Job with { TemporaryDirectory = temporary, ArchiveCopies = true,
            OneCMethod = nativeArchive ? OneCBackupMethod.FileArchive : OneCBackupMethod.PlatformDump,
            AdditionalDestinations = [new BackupDestination { DestinationDirectory = Path.Combine(root, "second copies") }] };
        var runner = new LocalBackupRunner(executor,
            new BackupCompletion(storage, new BackupCatalog(Path.Combine(root, "catalog.json"))), History, Path.Combine(root, "work"));
        var result = await runner.RunAsync(job);
        Assert.Equal(BackupRunStatus.Succeeded, result.Status);
        Assert.Equal(temporary, Path.GetDirectoryName(result.WorkDirectory));
        Assert.Equal(2, storage.Paths.Count);
        Assert.Single(storage.Paths.Distinct());
        Assert.StartsWith(result.WorkDirectory + Path.DirectorySeparatorChar, storage.Paths[0]);
        Assert.False(Directory.Exists(result.WorkDirectory));
        Assert.False(Directory.Exists(Path.Combine(root, "work")));
        Assert.Equal("unrelated file", await File.ReadAllTextAsync(marker));
        Assert.Equal("source database", await File.ReadAllTextAsync(Path.Combine(Job.Source, "1Cv8.1CD")));
        using var zip = ZipFile.OpenRead(result.Backup!.Location);
        using var reader = new StreamReader(Assert.Single(zip.Entries).Open());
        Assert.Equal(nativeArchive ? "source database" : "dump fixture", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task UnavailableTemporaryFolderFailsBeforeDumpWithoutFallingBackToDefault()
    {
        var temporary = Path.Combine(root, "blocked temporary");
        await File.WriteAllTextAsync(temporary, "keep this file");
        var process = new FakeProcess((_, _) => throw new Exception("Must not launch"));
        var result = await Runner(process, History).RunAsync(Job with { TemporaryDirectory = temporary });
        Assert.Equal(BackupRunStatus.Failed, result.Status);
        Assert.Equal(temporary, Path.GetDirectoryName(result.WorkDirectory));
        Assert.Equal("keep this file", await File.ReadAllTextAsync(temporary));
        Assert.False(Directory.Exists(Path.Combine(root, "work")));
        Assert.False(Directory.Exists(Job.DestinationDirectory));
        Assert.Equal(BackupRunStatus.Failed, Assert.Single(await History.ReadAsync()).Status);
    }

    [Fact]
    public async Task FailedDumpRemainsInSelectedTemporaryFolderForRecovery()
    {
        var temporary = Path.Combine(root, "HDD temporary");
        var process = new FakeProcess(async (command, ct) =>
        { await File.WriteAllTextAsync(command.Arguments[4], "partial", ct); return 1; });
        var result = await Runner(process, History).RunAsync(Job with { TemporaryDirectory = temporary });
        Assert.Equal(BackupRunStatus.Failed, result.Status);
        Assert.Equal(temporary, Path.GetDirectoryName(result.WorkDirectory));
        Assert.Equal("partial", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(result.WorkDirectory, "*.dt"))));
        Assert.False(Directory.Exists(Path.Combine(root, "work")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/folder")]
    [InlineData("\0")]
    public async Task InvalidTemporaryFolderIsRejectedBeforeCreatingFiles(string temporary)
    {
        var process = new FakeProcess((_, _) => throw new Exception("Must not launch"));
        await Assert.ThrowsAsync<InvalidDataException>(() => Runner(process, History).RunAsync(Job with { TemporaryDirectory = temporary }));
        Assert.False(File.Exists(Path.Combine(root, "runs.json")));
    }

    [Fact]
    public async Task TemporaryFolderSurvivesJobStoreRoundTripAndLegacyJobsUseDefault()
    {
        var store = new JobStore(Path.Combine(root, "jobs.json"));
        await store.SaveAsync([Job]);
        Assert.DoesNotContain("temporaryDirectory", await File.ReadAllTextAsync(Path.Combine(root, "jobs.json")), StringComparison.OrdinalIgnoreCase);
        Assert.Null(Assert.Single(await store.LoadAsync()).TemporaryDirectory);
        var temporary = Path.Combine(root, "HDD temporary");
        await store.SaveAsync([Job with { TemporaryDirectory = temporary }]);
        Assert.Equal(temporary, Assert.Single(await store.LoadAsync()).TemporaryDirectory);
    }

    private sealed class TrackingStorage : IBackupStorage
    {
        private readonly FileBackupStorage files = new();
        public List<string> Paths { get; } = [];
        public Task<StoredBackup> StoreAsync(string path, BackupJob job, CancellationToken token)
        { Paths.Add(path); return files.StoreAsync(path, job, token); }
        public Task VerifyAsync(StoredBackup backup, BackupJob job, CancellationToken token) => files.VerifyAsync(backup, job, token);
        public Task DeleteAsync(StoredBackup backup, BackupJob job, CancellationToken token) => files.DeleteAsync(backup, job, token);
    }

    [Fact]
    public async Task EmptyDumpIsRejected()
    {
        var process = new FakeProcess(async (command, ct) =>
        { await File.WriteAllBytesAsync(command.Arguments[4], [], ct); return 0; });
        var result = await Runner(process, History).RunAsync(Job);
        Assert.Equal(BackupRunStatus.Failed, result.Status);
        Assert.False(Directory.Exists(Job.DestinationDirectory));
    }

    [Fact]
    public async Task CancellationIsPersistedDespiteCancelledTokenAndDoesNotDeliverPartialDump()
    {
        using var cancellation = new CancellationTokenSource();
        var process = new FakeProcess(async (command, ct) =>
        {
            await File.WriteAllTextAsync(command.Arguments[4], "partial", ct);
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return 0;
        });
        var result = await Runner(process, History).RunAsync(Job, cancellation.Token);
        Assert.Equal(BackupRunStatus.Cancelled, result.Status);
        Assert.Equal(BackupRunStatus.Cancelled, Assert.Single(await History.ReadAsync()).Status);
        Assert.True(Directory.Exists(result.WorkDirectory));
        Assert.False(Directory.Exists(Job.DestinationDirectory));
    }

    [Fact]
    public async Task DeliveryFailurePreservesCompletedDumpForRecovery()
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Job.DestinationDirectory, "file blocking destination directory");
        var process = new FakeProcess(async (command, ct) =>
        { await File.WriteAllTextAsync(command.Arguments[4], "complete", ct); return 0; });
        var result = await Runner(process, History).RunAsync(Job);
        Assert.Equal(BackupRunStatus.Failed, result.Status);
        Assert.Single(Directory.GetFiles(result.WorkDirectory, "*.dt"));
        Assert.Empty(await new BackupCatalog(Path.Combine(root, "catalog.json")).ReadAsync());
    }

    [Fact]
    public async Task CorruptHistoryPreventsPlatformLaunchAndIsNotOverwritten()
    {
        var path = Path.Combine(root, "runs.json");
        await File.WriteAllTextAsync(path, "broken history");
        var process = new FakeProcess((_, _) => throw new Exception("Must not launch"));
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => Runner(process, History).RunAsync(Job));
        Assert.Equal("broken history", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task StartupMarksOnlyUnfinishedRunsAsInterrupted()
    {
        var history = History;
        var pending = new BackupRun(Guid.NewGuid(), Job.Id, Job.Name, DateTimeOffset.UtcNow, null,
            BackupRunStatus.Running, Path.Combine(root, "work", "old"));
        var finished = pending with { RunId = Guid.NewGuid(), FinishedUtc = DateTimeOffset.UtcNow, Status = BackupRunStatus.Failed };
        await history.SaveAsync(pending);
        await history.SaveAsync(finished);
        await history.RecoverInterruptedAsync();
        var runs = await History.ReadAsync();
        Assert.Equal(BackupRunStatus.Interrupted, runs.Single(r => r.RunId == pending.RunId).Status);
        Assert.Equal(BackupRunStatus.Failed, runs.Single(r => r.RunId == finished.RunId).Status);
    }

    [Fact]
    public async Task ConcurrentRequestsDoNotOverlapPlatformExecution()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var process = new FakeProcess(async (command, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            { entered.SetResult(); await release.Task.WaitAsync(ct); }
            await File.WriteAllTextAsync(command.Arguments[4], "complete", ct);
            return 0;
        });
        var runner = Runner(process, History);
        var first = runner.RunAsync(Job);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = runner.RunAsync(Job);
        Assert.Equal(1, Volatile.Read(ref calls));
        release.SetResult();
        Assert.All(await Task.WhenAll(first, second), r => Assert.Equal(BackupRunStatus.Succeeded, r.Status));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10081)]
    public void InvalidTimeoutIsRejected(int timeout) => Assert.NotEmpty((Job with
        { OneC = Job.OneC! with { TimeoutMinutes = timeout } }).Validate());

    [Fact]
    public async Task RealProcessRunnerUsesArgumentListAndReturnsExitCode()
    {
        var command = OperatingSystem.IsWindows()
            ? new BackupProcess(Environment.GetEnvironmentVariable("COMSPEC")!, ["/c", "exit", "7"], root)
            : new BackupProcess("/bin/sh", ["-c", "test \"$1\" = 'path with spaces' && exit 7; exit 9", "fixture", "path with spaces"], root);
        Assert.Equal(7, await new BackupProcessRunner().RunAsync(command, CancellationToken.None));
    }

    [Fact]
    public async Task RealProcessCancellationWaitsForExit()
    {
        if (OperatingSystem.IsWindows()) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var command = new BackupProcess("/bin/sh", ["-c", "echo $$ > process.pid; exec sleep 60"], root);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BackupProcessRunner().RunAsync(command, cancellation.Token));
        var pid = int.Parse(await File.ReadAllTextAsync(Path.Combine(root, "process.pid")));
        Assert.False(Directory.Exists($"/proc/{pid}"));
    }

    private sealed class FakeProcess(Func<BackupProcess, CancellationToken, Task<int>> run) : IBackupProcessRunner
    {
        public Task<int> RunAsync(BackupProcess command, CancellationToken cancellationToken) => run(command, cancellationToken);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
