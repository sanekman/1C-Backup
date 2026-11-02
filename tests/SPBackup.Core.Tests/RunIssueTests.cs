using System.ComponentModel;
using System.Text.Json;
using SPBackup.Core;
using SPBackup.Core.Monitoring;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class RunIssueTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sp-run-issues-" + Guid.NewGuid().ToString("N"));
    public RunIssueTests() => Directory.CreateDirectory(root);
    private BackupJob Job => new() { Name = "Files", Source = "database", DestinationDirectory = Path.Combine(root, "copies"), KeepLast = 1 };
    private BackupRunHistory History => new(Path.Combine(root, "runs.json"));
    private LocalBackupRunner Runner(IBackupStorage storage, IBackupExecutor? executor = null) => new(executor ?? new Dump(),
        new(storage, new BackupCatalog(Path.Combine(root, "catalog.json"))), History, Path.Combine(root, "work"));

    [Fact]
    public async Task NestedNasErrorIsPersistedAndReportedWithoutExceptionText()
    {
        var run = await Runner(new NasFailure()).RunAsync(Job);
        Assert.Equal(BackupRunStatus.Failed, run.Status);
        var saved = Assert.Single(await History.ReadAsync());
        var report = ServerMonitoringDispatcher.ToReport(saved);
        var issue = Assert.Single(report.Issues!);
        Assert.Equal("NetworkLogonFailed", issue.Code); Assert.Equal(1326, issue.NativeCode);
        Assert.Equal("Error", issue.Severity); Assert.Equal("Storage", issue.Stage);
        Assert.True(MonitoringValidation.Valid(report));
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(report));
        Assert.DoesNotContain("sp-run-issues-", JsonSerializer.Serialize(report));
    }

    [Fact]
    public async Task ConcurrentDestinationsKeepSeparateReasonsAndSuccessfulDelivery()
    {
        var job = Job with { AdditionalDestinations = [new() { DestinationDirectory = Path.Combine(root, "failed") }] };
        var run = await Runner(new NasFailure { FailOnly = "failed" }).RunAsync(job);
        Assert.Equal(BackupRunStatus.PartiallySucceeded, run.Status);
        Assert.True(File.Exists(run.Backup!.Location));
        var issue = Assert.Single(ServerMonitoringDispatcher.ToReport(run).Issues!);
        Assert.Equal(1, issue.DestinationIndex); Assert.Equal("NetworkLogonFailed", issue.Code);
        Assert.Equal(2, run.Destinations!.Count);
    }

    [Fact]
    public async Task RetentionWarningExplainsChainPreservationAndDoesNotExposePaths()
    {
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "data.txt"), "one");
        var job = Job with { Kind = DatabaseKind.FilesAndFolders, Source = source, FileBackupMode = FileBackupMode.Incremental, FullBackupEvery = 7 };
        var runner = Runner(new FileBackupStorage());
        await runner.RunAsync(job);
        await File.WriteAllTextAsync(Path.Combine(source, "data.txt"), "two");
        var run = await runner.RunAsync(job);
        Assert.Equal(BackupRunStatus.Succeeded, run.Status);
        Assert.Contains(run.Issues!, i => i.Code == "RetentionChainPreserved" && i.DestinationIndex == 0 && i.Severity == "Warning");
        Assert.Equal(2, Directory.GetFiles(job.DestinationDirectory).Length);
    }

    [Fact]
    public async Task SourceFailureIsDifferentFromStorageAndUnknownTextStaysLocal()
    {
        var run = await Runner(new FileBackupStorage(), new SourceFailure()).RunAsync(Job);
        var issue = Assert.Single(run.Issues!);
        Assert.Equal("Source", issue.Stage); Assert.Equal("BackupFailed", issue.Code);
        Assert.DoesNotContain("password=secret", JsonSerializer.Serialize(ServerMonitoringDispatcher.ToReport(run)));
    }

    [Fact]
    public async Task IndependentAndParallelScopesDoNotMixOrGrowWithoutBound()
    {
        using var scope = BackupRunIssueContext.Begin();
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() =>
            BackupRunIssueContext.Report(new Win32Exception(1326, "secret"), "Storage", "Error", i))));
        Assert.Equal(100, scope.Snapshot().Count);
        using (var other = BackupRunIssueContext.Begin())
        {
            BackupRunIssueContext.Report("Cancelled", "Backup");
            Assert.Equal("Cancelled", Assert.Single(other.Snapshot()).Code);
        }
        Assert.Equal(100, scope.Snapshot().Count);
        for (var i = 0; i < 1000; i++) BackupRunIssueContext.Report(new Win32Exception(i), "Retention");
        Assert.InRange(scope.Snapshot().Count, 100, 256);
    }

    [Fact]
    public void CapacityFailureIsTypedRatherThanParsedFromLocalizedText()
    {
        using var scope = BackupRunIssueContext.Begin();
        var error = Assert.Throws<IOException>(() => BackupDiskSpace.RequireAvailable(root, 100, _ => 1));
        BackupRunIssueContext.Report(error, "Storage", "Error");
        Assert.Equal("DiskFull", Assert.Single(scope.Snapshot()).Code);
    }

    private sealed class Dump : IBackupExecutor
    {
        public async Task<string> CreateAsync(BackupJob job, string workDirectory, CancellationToken token)
        { var path = Path.Combine(workDirectory, "fixture.bak"); await File.WriteAllTextAsync(path, "backup", token); return path; }
    }
    private sealed class SourceFailure : IBackupExecutor
    {
        public Task<string> CreateAsync(BackupJob job, string workDirectory, CancellationToken token) => throw new Exception("password=secret");
    }
    private sealed class NasFailure : IBackupStorage
    {
        public string? FailOnly;
        private readonly FileBackupStorage inner = new();
        public Task PrepareAsync(BackupJob job, CancellationToken token)
        {
            if (FailOnly is null || Path.GetFileName(job.DestinationDirectory) == FailOnly)
                throw new IOException("private secret path", new Win32Exception(1326, "password=secret"));
            return ((IBackupStorage)inner).PrepareAsync(job, token);
        }
        public Task<StoredBackup> StoreAsync(string file, BackupJob job, CancellationToken token) => inner.StoreAsync(file, job, token);
        public Task VerifyAsync(StoredBackup backup, BackupJob job, CancellationToken token) => inner.VerifyAsync(backup, job, token);
        public Task DeleteAsync(StoredBackup backup, BackupJob job, CancellationToken token) => inner.DeleteAsync(backup, job, token);
    }
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }
}
