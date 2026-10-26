using System.Net;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class LocalStorageCheckTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sp-agent-check-" + Guid.NewGuid().ToString("N"));
    private readonly Guid session = Guid.NewGuid();
    private readonly TrackingStorage storage = new();
    private readonly CancellationTokenSource stop = new();
    private Task? loop;
    private LocalStorageCheckQueue Queue => new(root);
    private JobStore Jobs => new(Path.Combine(root, "jobs.json"), useLocalTime: true);
    private BackupJob Job => new() { Id = jobId, Name = "Проверка", Kind = DatabaseKind.FilesAndFolders,
        Source = Path.Combine(root, "source.txt"), DestinationDirectory = Path.Combine(root, "copies") };
    private readonly Guid jobId = Guid.NewGuid();
    public LocalStorageCheckTests() { Directory.CreateDirectory(root); File.WriteAllText(Job.Source, "private source"); }
    private async Task Start(Guid? hostSession = null)
    {
        await Jobs.SaveAsync([Job]);
        var now = DateTimeOffset.UtcNow;
        await new LocalAgentControlStore(root).WriteAsync(new(hostSession ?? session, Environment.ProcessId, now, now, LocalAgentPhase.Running) { CanCheckStorages = true });
        loop = new LocalStorageCheckProcessor(root, Jobs, new(storage, Path.Combine(root, "work")))
            .RunAsync(hostSession ?? session, _ => { }, stop.Token);
    }
    private async Task<LocalStorageCheckReceipt> Finish(LocalStorageCheckRequest request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var receipt = await Queue.ReadAsync(request, timeout.Token);
            if (receipt is { Phase: not LocalStorageCheckPhase.Accepted }) return receipt;
            await Task.Delay(20, timeout.Token);
        }
    }
    private async Task Stop()
    {
        stop.Cancel();
        if (loop is not null) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
        loop = null;
    }

    [Fact]
    public async Task LiveHostChecksUnderItsIdentityWithoutReadingSourceOrChangingBackupHistory()
    {
        await Start();
        await File.WriteAllTextAsync(Path.Combine(root, "runs.json"), "history marker");
        await File.WriteAllTextAsync(Path.Combine(root, "copies.json"), "catalog marker");
        var jobs = await File.ReadAllBytesAsync(Path.Combine(root, "jobs.json"));
        using var lease = new BackupEngineCoordinator(root).Acquire();
        await using var held = new FileStream(Job.Source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var request = await Queue.EnqueueAsync(session, Job);
        var report = await Queue.WaitAsync(request);
        Assert.True(Assert.Single(report.Destinations).Succeeded);
        Assert.Equal(StorageCheckIdentity.Current(), report.ExecutionIdentity);
        Assert.Equal(1, storage.Stores); Assert.Equal(1, storage.Deletes);
        Assert.Equal("history marker", await File.ReadAllTextAsync(Path.Combine(root, "runs.json")));
        Assert.Equal("catalog marker", await File.ReadAllTextAsync(Path.Combine(root, "copies.json")));
        Assert.Equal(jobs, await File.ReadAllBytesAsync(Path.Combine(root, "jobs.json")));
        Assert.Empty(await new LocalManualRunQueue(root).PendingAsync());
        Assert.Empty(Directory.GetFiles(Job.DestinationDirectory));
        Assert.Empty(Directory.GetDirectories(Path.Combine(root, "work")));
        await Stop();
    }

    [Fact]
    public async Task StaleSessionCannotReplayAProbeInTheNewHost()
    {
        var request = await Queue.EnqueueAsync(Guid.NewGuid(), Job);
        await Start(); var receipt = await Finish(request);
        Assert.Equal(LocalStorageCheckPhase.Rejected, receipt.Phase); Assert.Equal(0, storage.Stores);
        Assert.False(Directory.Exists(Job.DestinationDirectory)); await Stop();
    }

    [Fact]
    public async Task ChangedSavedJobIsRejectedBeforeFilesystemProbe()
    {
        await Jobs.SaveAsync([Job]); var request = await Queue.EnqueueAsync(session, Job with { DestinationDirectory = Path.Combine(root, "old") });
        await Start(); var receipt = await Finish(request);
        Assert.Equal(LocalStorageCheckPhase.Rejected, receipt.Phase); Assert.Contains("изменились", receipt.Error);
        Assert.Equal(0, storage.Stores); Assert.False(Directory.Exists(Path.Combine(root, "old"))); await Stop();
    }

    [Fact]
    public async Task CancelBeforeAcceptanceCreatesNoProbe()
    {
        var request = await Queue.EnqueueAsync(session, Job); await Queue.CancelAsync(request);
        await Start(); var receipt = await Finish(request);
        Assert.Equal(LocalStorageCheckPhase.Cancelled, receipt.Phase); Assert.Equal(0, storage.Stores);
        Assert.False(Directory.Exists(Job.DestinationDirectory)); await Stop();
    }

    [Fact]
    public async Task CancellingClientWaitingSignalsTheActiveProbeAndCleansItsWork()
    {
        storage.Block = true; await Start(); var request = await Queue.EnqueueAsync(session, Job);
        using var caller = new CancellationTokenSource(); var waiting = Queue.WaitAsync(request, caller.Token);
        await storage.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        var receipt = await Finish(request); Assert.Equal(LocalStorageCheckPhase.Cancelled, receipt.Phase);
        Assert.True(receipt.Report!.Cancelled); Assert.Empty(Directory.GetDirectories(Path.Combine(root, "work")));
        Assert.Equal(1, storage.Stores); await Stop();
    }

    [Fact]
    public async Task DuplicateRequestCannotRepeatProbeOrReplaceCompletedReport()
    {
        await Start(); var request = await Queue.EnqueueAsync(session, Job); var first = await Finish(request);
        var pending = Path.Combine(root, "storage-checks", "pending", request.RequestId.ToString("N") + ".json");
        await new AgentJsonFile<LocalStorageCheckRequest>(pending).WriteAsync(request, default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (File.Exists(pending)) await Task.Delay(20, timeout.Token);
        var again = await Queue.ReadAsync(request);
        Assert.Equal(LocalStorageCheckPhase.Completed, again!.Phase); Assert.Equal(first.JobFingerprint, again.JobFingerprint);
        Assert.Equal(1, storage.Stores); await Stop();
    }

    [Fact]
    public async Task ExpiredRequestDoesNotExecuteLater()
    {
        var request = await Queue.EnqueueAsync(session, Job);
        await new AgentJsonFile<LocalStorageCheckRequest>(Path.Combine(root, "storage-checks", "pending", request.RequestId.ToString("N") + ".json"))
            .WriteAsync(request with { SubmittedUtc = DateTimeOffset.UtcNow.AddMinutes(-4) }, default);
        await Start(); Assert.Equal(LocalStorageCheckPhase.Rejected, (await Finish(request)).Phase);
        Assert.Equal(0, storage.Stores); await Stop();
    }

    [Fact]
    public async Task MalformedCancellationCannotStopTheAgentOrLoseTheTerminalResponse()
    {
        storage.Block = true; await Start(); var request = await Queue.EnqueueAsync(session, Job);
        await storage.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var cancel = Path.Combine(root, "storage-checks", "cancel", request.RequestId.ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(cancel)!); await File.WriteAllTextAsync(cancel, "broken JSON");
        Assert.Equal(LocalStorageCheckPhase.Rejected, (await Finish(request)).Phase);
        Assert.False(loop!.IsCompleted); storage.Block = false;
        var next = await Queue.EnqueueAsync(session, Job);
        Assert.Equal(LocalStorageCheckPhase.Completed, (await Finish(next)).Phase); await Stop();
    }

    [Fact]
    public async Task FailedFinalReceiptIsRetriedWithoutRepeatingTheProbe()
    {
        storage.Block = true; await Start(); var request = await Queue.EnqueueAsync(session, Job);
        await storage.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var receipts = Path.Combine(root, "storage-checks", "receipts"); Directory.Delete(receipts, true);
        await File.WriteAllTextAsync(receipts, "blocked receipt directory"); storage.Release.TrySetResult();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (storage.Deletes == 0) await Task.Delay(20, timeout.Token);
        File.Delete(receipts); Directory.CreateDirectory(receipts);
        Assert.Equal(LocalStorageCheckPhase.Completed, (await Finish(request)).Phase);
        Assert.Equal(1, storage.Stores); Assert.Equal(1, storage.Deletes); await Stop();
    }

    [Fact]
    public async Task OneBlockedReceiptCannotStarveAnotherCheck()
    {
        var first = await Queue.EnqueueAsync(session, Job);
        var blocked = Path.Combine(root, "storage-checks", "receipts", first.RequestId.ToString("N") + ".json");
        Directory.CreateDirectory(blocked);
        var second = await Queue.EnqueueAsync(session, Job);
        await Start();
        Assert.Equal(LocalStorageCheckPhase.Completed, (await Finish(second)).Phase);
        Assert.Equal(1, storage.Stores);
        Directory.Delete(blocked);
        Assert.Equal(LocalStorageCheckPhase.Completed, (await Finish(first)).Phase);
        Assert.Equal(2, storage.Stores); await Stop();
    }

    [Fact]
    public async Task ReceiptFromAnotherRequestOrSessionIsNotTrusted()
    {
        var request = await Queue.EnqueueAsync(session, Job);
        var receipt = new LocalStorageCheckReceipt(request.RequestId, Guid.NewGuid(), Job.Id, request.JobFingerprint, LocalStorageCheckPhase.Rejected);
        await Queue.WriteAsync(receipt, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => Queue.ReadAsync(request));
    }

    [Fact]
    public void OldOrStaleOrFutureAgentCannotAdvertiseStorageChecksByManualCapabilityAlone()
    {
        var now = DateTimeOffset.UtcNow;
        var old = System.Text.Json.JsonSerializer.Deserialize<LocalAgentState>(System.Text.Json.JsonSerializer.Serialize(
            new { SessionId = session, ProcessId = Environment.ProcessId, StartedUtc = now.AddMinutes(-1), UpdatedUtc = now,
                Phase = 0, CanRunManually = true, RunsSchedules = true }))!;
        Assert.False(LocalStorageCheckQueue.IsAvailable(old, now));
        var current = old with { CanCheckStorages = true };
        Assert.True(LocalStorageCheckQueue.IsAvailable(current, now));
        Assert.False(LocalStorageCheckQueue.IsAvailable(current with { UpdatedUtc = now.AddSeconds(-20) }, now));
        Assert.False(LocalStorageCheckQueue.IsAvailable(current with { UpdatedUtc = now.AddSeconds(1) }, now));
        Assert.False(LocalStorageCheckQueue.IsAvailable(current with { Phase = LocalAgentPhase.Stopping }, now));
        Assert.False(LocalStorageCheckQueue.IsAvailable(current with { SessionId = Guid.Empty }, now));
    }

    [Fact]
    public async Task CorruptJobFileRejectsProbeWithoutStoppingRequestProcessor()
    {
        await Start(); await File.WriteAllTextAsync(Path.Combine(root, "jobs.json"), "broken JSON");
        var request = await Queue.EnqueueAsync(session, Job);
        Assert.Equal(LocalStorageCheckPhase.Rejected, (await Finish(request)).Phase);
        Assert.Equal(0, storage.Stores); Assert.False(loop!.IsCompleted);
        await Jobs.SaveAsync([Job]); var next = await Queue.EnqueueAsync(session, Job);
        Assert.Equal(LocalStorageCheckPhase.Completed, (await Finish(next)).Phase); await Stop();
    }

    [Fact]
    public async Task HostRestartEndsClientWaitWithoutRunningBackupCommands()
    {
        var request = await Queue.EnqueueAsync(session, Job);
        var now = DateTimeOffset.UtcNow;
        await new LocalAgentControlStore(root).WriteAsync(new(Guid.NewGuid(), Environment.ProcessId, now, now, LocalAgentPhase.Running) { CanCheckStorages = true });
        await Assert.ThrowsAsync<IOException>(() => Queue.WaitAsync(request));
        Assert.True(await Queue.IsCancelledAsync(request, default));
        Assert.Empty(await new LocalManualRunQueue(root).PendingAsync());
    }

    [Fact]
    public async Task ReportWithoutExecutingIdentityCannotConfirmAccess()
    {
        var request = await Queue.EnqueueAsync(session, Job);
        var report = new StorageCheckReport(root, null, [new("local", true, null)], false);
        await Queue.WriteAsync(new(request.RequestId, session, Job.Id, request.JobFingerprint, LocalStorageCheckPhase.Completed, report), default);
        await Assert.ThrowsAsync<InvalidDataException>(() => Queue.ReadAsync(request));
    }

    [Fact]
    public async Task StopDuringProbePersistsInterruptedResultAndCleansItsWork()
    {
        storage.Block = true; await Start(); var request = await Queue.EnqueueAsync(session, Job);
        await storage.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); await Stop();
        var receipt = await Queue.ReadAsync(request);
        Assert.Equal(LocalStorageCheckPhase.Interrupted, receipt!.Phase); Assert.True(receipt.Report!.Cancelled);
        Assert.Empty(Directory.GetDirectories(Path.Combine(root, "work")));
    }

    [Fact]
    public async Task RealBackgroundHostAdvertisesCapabilityAndProcessesProbeWithoutBackupRun()
    {
        await Jobs.SaveAsync([Job with { ScheduleKind = ScheduleKind.Manual }]);
        var expected = (await Jobs.LoadAsync()).Single();
        loop = new LocalAgentHost(root, new MustNotExecute(), new NoCredentials()).RunAsync(cancellationToken: stop.Token, pollInterval: TimeSpan.FromMilliseconds(30));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        LocalAgentState? state;
        while ((state = await new LocalAgentControlStore(root).ReadAsync(timeout.Token))?.CanCheckStorages != true) await Task.Delay(20, timeout.Token);
        var request = await Queue.EnqueueAsync(state.SessionId, expected);
        var result = await Queue.WaitAsync(request, timeout.Token);
        Assert.True(Assert.Single(result.Destinations).Succeeded);
        Assert.Empty(await new BackupRunHistory(Path.Combine(root, "runs.json")).ReadAsync());
        Assert.Empty(await new BackupCatalog(Path.Combine(root, "copies.json")).ReadAsync());
        stop.Cancel(); Assert.Equal(0, await ((Task<int>)loop)); loop = null;
    }

    private sealed class MustNotExecute : IBackupExecutor
    { public Task<string> CreateAsync(BackupJob job, string work, CancellationToken token) => throw new InvalidOperationException("Probe cannot run a backup"); }
    private sealed class NoCredentials : IFtpCredentialStore
    { public Task<NetworkCredential> GetAsync(string key, CancellationToken token = default) => throw new InvalidOperationException("Local probe requires no credentials"); }
    private sealed class TrackingStorage : IBackupStorage
    {
        private readonly FileBackupStorage files = new();
        internal bool Block;
        internal int Stores, Deletes;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<StoredBackup> StoreAsync(string path, BackupJob job, CancellationToken token = default)
        {
            Interlocked.Increment(ref Stores); Entered.TrySetResult();
            if (Block) await Release.Task.WaitAsync(token);
            return await files.StoreAsync(path, job, token);
        }
        public Task VerifyAsync(StoredBackup backup, BackupJob job, CancellationToken token = default) => files.VerifyAsync(backup, job, token);
        public async Task DeleteAsync(StoredBackup backup, BackupJob job, CancellationToken token = default)
        { await files.DeleteAsync(backup, job, token); Interlocked.Increment(ref Deletes); }
    }
    public void Dispose()
    {
        stop.Cancel();
        try { loop?.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        stop.Dispose(); Directory.Delete(root, true);
    }
}
