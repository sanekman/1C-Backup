using System.Net;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class LocalAgentHostTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OneCBackup-host-" + Guid.NewGuid());
    public LocalAgentHostTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void EngineLeaseIsSharedWithinHostAndExclusiveBetweenHosts()
    {
        var first = new BackupEngineCoordinator(directory);
        var second = new BackupEngineCoordinator(directory);
        var lease = first.Acquire();
        var nested = first.Acquire();
        Assert.False(first.IsHeldElsewhere());
        Assert.True(second.IsHeldElsewhere());
        Assert.Throws<BackupEngineBusyException>(() => second.Acquire());
        lease.Dispose(); lease.Dispose();
        Assert.True(second.IsHeldElsewhere());
        nested.Dispose();
        Assert.False(second.IsHeldElsewhere());
        using var next = second.Acquire();
    }

    [Fact]
    public async Task OldStopRequestCannotStopNewSession()
    {
        var control = new LocalAgentControlStore(directory);
        var old = Guid.NewGuid();
        await control.RequestStopAsync(old);
        Assert.True(await control.StopRequestedAsync(old));
        Assert.False(await control.StopRequestedAsync(Guid.NewGuid()));
    }

    private async Task<BackupJob> CreateJob()
    {
        var source = Path.Combine(directory, "base");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "1Cv8.1CD"), "test database bytes");
        var job = new BackupJob { Name = "Файловая база", Source = source, OneCMethod = OneCBackupMethod.FileArchive,
            DestinationDirectory = Path.Combine(directory, "copies"), ScheduleKind = ScheduleKind.Manual };
        await new JobStore(Path.Combine(directory, "jobs.json")).SaveAsync([job]);
        return job;
    }

    [Fact]
    public async Task HeadlessManualRunCreatesZipHistoryAndCatalogAndReleasesOwnership()
    {
        var job = await CreateJob();
        var host = new LocalAgentHost(directory, new DatabaseBackupExecutor(new BackupProcessRunner()), new NoCredentials());
        Assert.Equal(0, await host.RunAsync(job.Id));
        Assert.False((await new LocalAgentControlStore(directory).ReadAsync())!.RunsSchedules);
        var run = Assert.Single(await new BackupRunHistory(Path.Combine(directory, "runs.json")).ReadAsync());
        Assert.Equal(BackupRunStatus.Succeeded, run.Status);
        var progress = await new BackupProgressStore(directory).ReadAsync();
        Assert.NotNull(progress);
        Assert.Equal(run.RunId, progress.RunId);
        Assert.Equal(BackupStage.Completed, progress.Progress.Stage);
        Assert.Equal(100, progress.Progress.Percent);
        var record = Assert.Single(await new BackupCatalog(Path.Combine(directory, "copies.json")).ReadAsync());
        Assert.EndsWith(".1CD.zip", record.Backup.Location);
        await new BackupCopyVerifier(new FileBackupStorage()).VerifyAsync(record, job);
        Assert.Equal(LocalAgentPhase.Stopped, (await new LocalAgentControlStore(directory).ReadAsync())!.Phase);
        Assert.False(new BackupEngineCoordinator(directory).IsHeldElsewhere());
    }

    [Fact]
    public async Task RestartRecoversHistoryWithoutReplayingOldCommandOrChangingDeliveredCopy()
    {
        var job = await CreateJob();
        Assert.Equal(0, await new LocalAgentHost(directory, new DatabaseBackupExecutor(new BackupProcessRunner()),
            new NoCredentials()).RunAsync(job.Id));
        var history = new BackupRunHistory(Path.Combine(directory, "runs.json"));
        var completed = Assert.Single(await history.ReadAsync());
        var catalogPath = Path.Combine(directory, "copies.json");
        var catalogBefore = await File.ReadAllBytesAsync(catalogPath);
        var copy = Assert.Single(await new BackupCatalog(catalogPath).ReadAsync());
        var archiveBefore = await File.ReadAllBytesAsync(copy.Backup.Location);
        var unfinished = completed with { RunId = Guid.NewGuid(), Status = BackupRunStatus.Running,
            FinishedUtc = null, Backup = null, Destinations = null };
        await history.SaveAsync(unfinished);
        var oldSession = (await new LocalAgentControlStore(directory).ReadAsync())!.SessionId;
        var queue = new LocalManualRunQueue(directory);
        var oldCommand = await queue.EnqueueAsync(oldSession, job.Id);
        // A crash can leave an acceptance after its command was consumed. It must receive a
        // terminal result without needing the command to be delivered again.
        var abandoned = await queue.EnqueueAsync(oldSession, job.Id);
        await queue.WriteReceiptAsync(new(abandoned.RequestId, oldSession, job.Id, LocalManualRunPhase.Accepted));
        queue.Consume(abandoned.RequestId);
        // A restored command can outlive its already recorded result. Restart must preserve
        // the actual completion instead of changing it to a stale-session rejection.
        var deliveredCommand = await queue.EnqueueAsync(oldSession, job.Id);
        await queue.WriteReceiptAsync(new(deliveredCommand.RequestId, oldSession, job.Id, LocalManualRunPhase.Finished));
        var receiptPath = Path.Combine(directory, "manual-runs", "receipts", deliveredCommand.RequestId.ToString("N") + ".json");
        var receiptBefore = await File.ReadAllBytesAsync(receiptPath);
        using var stop = new CancellationTokenSource();
        var host = new LocalAgentHost(directory, new DatabaseBackupExecutor(new BackupProcessRunner()),
            new NoCredentials()).RunAsync(pollInterval: TimeSpan.FromMilliseconds(50), cancellationToken: stop.Token);
        try
        {
            await WaitForReceipt(queue, oldCommand.RequestId, LocalManualRunPhase.Rejected);
            await WaitForReceipt(queue, abandoned.RequestId, LocalManualRunPhase.Interrupted);
            await WaitForConsumed(queue, deliveredCommand.RequestId);
            Assert.Equal(LocalManualRunPhase.Finished, (await queue.ReadReceiptAsync(deliveredCommand.RequestId))!.Phase);
            Assert.Equal(receiptBefore, await File.ReadAllBytesAsync(receiptPath));
            var runs = await history.ReadAsync();
            Assert.Equal(2, runs.Count);
            Assert.Equal(BackupRunStatus.Interrupted, runs.Single(r => r.RunId == unfinished.RunId).Status);
            Assert.Equal(BackupRunStatus.Succeeded, runs.Single(r => r.RunId == completed.RunId).Status);
            Assert.Equal(catalogBefore, await File.ReadAllBytesAsync(catalogPath));
            Assert.Equal(archiveBefore, await File.ReadAllBytesAsync(copy.Backup.Location));
            await new BackupCopyVerifier(new FileBackupStorage()).VerifyAsync(copy, job);
            Assert.False(host.IsCompleted);
        }
        finally { stop.Cancel(); Assert.Equal(0, await host.WaitAsync(TimeSpan.FromSeconds(10))); }
        Assert.False(new BackupEngineCoordinator(directory).IsHeldElsewhere());
    }

    [Fact]
    public async Task PartialManualDeliveryKeepsLocalCopyAndReturnsFailureToTheCaller()
    {
        var job = (await CreateJob()) with { AdditionalDestinations = [new BackupDestination
        {
            StorageKind = StorageKind.Ftp, DestinationDirectory = "/backups",
            Ftp = new FtpSettings { Host = "localhost", CredentialKey = "unavailable" }
        }] };
        await new JobStore(Path.Combine(directory, "jobs.json")).SaveAsync([job]);
        var host = new LocalAgentHost(directory, new DatabaseBackupExecutor(new BackupProcessRunner()), new NoCredentials());
        Assert.Equal(1, await host.RunAsync(job.Id));
        var run = Assert.Single(await new BackupRunHistory(Path.Combine(directory, "runs.json")).ReadAsync());
        Assert.Equal(BackupRunStatus.PartiallySucceeded, run.Status);
        var progress = await new BackupProgressStore(directory).ReadAsync();
        Assert.Equal(BackupStage.PartiallySucceeded, progress!.Progress.Stage);
        Assert.Null(progress.Progress.Percent);
        Assert.Equal(BackupStage.Failed, progress.Destinations[1].Progress.Stage);
        Assert.Equal(2, run.Destinations!.Count);
        var record = Assert.Single(await new BackupCatalog(Path.Combine(directory, "copies.json")).ReadAsync());
        Assert.True(File.Exists(record.Backup.Location));
        Assert.Equal(LocalAgentPhase.Faulted, (await new LocalAgentControlStore(directory).ReadAsync())!.Phase);
        Assert.False(new BackupEngineCoordinator(directory).IsHeldElsewhere());
    }

    [Fact]
    public async Task FullStopWithoutAnOwnerDoesNotCreateAStopRequest()
    {
        Assert.Null(await new LocalAgentStopper(directory).StopAsync(TimeSpan.FromSeconds(1)));
        Assert.False(await new LocalAgentControlStore(directory).StopRequestedAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task FullStopDoesNotClaimSuccessWhileAnUnknownExecutorHoldsTheLease()
    {
        using var lease = new BackupEngineCoordinator(directory).Acquire();
        await Assert.ThrowsAsync<TimeoutException>(() =>
            new LocalAgentStopper(directory).StopAsync(TimeSpan.FromMilliseconds(100)));
        Assert.True(new BackupEngineCoordinator(directory).IsHeldElsewhere());
    }

    [Fact]
    public async Task FullStopPreservesCallerCancellation()
    {
        using var lease = new BackupEngineCoordinator(directory).Acquire();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LocalAgentStopper(directory).StopAsync(TimeSpan.FromSeconds(1), cancellation.Token));
        Assert.True(new BackupEngineCoordinator(directory).IsHeldElsewhere());
    }

    [Fact]
    public async Task StopCancelsRunningBackupAndWaitsForItsTerminalHistory()
    {
        var job = await CreateJob();
        var executor = new BlockingExecutor();
        var host = new LocalAgentHost(directory, executor, new NoCredentials());
        var task = host.RunAsync(job.Id);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var control = new LocalAgentControlStore(directory);
        var state = (await control.ReadAsync())!;
        Assert.True(new BackupEngineCoordinator(directory).IsHeldElsewhere());
        var stopped = await new LocalAgentStopper(directory).StopAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(state.SessionId, stopped!.SessionId);
        Assert.Equal(0, await task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(BackupRunStatus.Cancelled, Assert.Single(await new BackupRunHistory(Path.Combine(directory, "runs.json")).ReadAsync()).Status);
        Assert.Equal(LocalAgentPhase.Stopped, (await control.ReadAsync())!.Phase);
        Assert.False(new BackupEngineCoordinator(directory).IsHeldElsewhere());
    }

    [Fact]
    public async Task SecondHostCannotRewriteRunningStateOrRecoverItsHistory()
    {
        var job = await CreateJob();
        var executor = new BlockingExecutor();
        using var cancellation = new CancellationTokenSource();
        var task = new LocalAgentHost(directory, executor, new NoCredentials()).RunAsync(job.Id, cancellationToken: cancellation.Token);
        try
        {
            await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var control = new LocalAgentControlStore(directory);
            var session = (await control.ReadAsync())!.SessionId;
            await Assert.ThrowsAsync<BackupEngineBusyException>(() =>
                new LocalAgentHost(directory, executor, new NoCredentials()).RunAsync(job.Id));
            Assert.Equal(session, (await control.ReadAsync())!.SessionId);
            Assert.Equal(BackupRunStatus.Running, Assert.Single(await new BackupRunHistory(Path.Combine(directory, "runs.json")).ReadAsync()).Status);
        }
        finally { cancellation.Cancel(); await task.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task ManagedModeIsRejectedBeforeStartingBackup()
    {
        var job = await CreateJob();
        await new AgentSettingsStore(Path.Combine(directory, "agent.json")).SaveAsync(new AgentSettings
            { Mode = AgentMode.Managed, ServerAddress = "https://backup.example.test", CredentialKey = "agent-key" });
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            new LocalAgentHost(directory, new BlockingExecutor(), new NoCredentials()).RunAsync(job.Id));
        Assert.False(File.Exists(Path.Combine(directory, "runs.json")));
        Assert.False(new BackupEngineCoordinator(directory).IsHeldElsewhere());
    }

    private sealed class NoCredentials : IFtpCredentialStore
    {
        public Task<NetworkCredential> GetAsync(string key, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected FTP access");
    }
    private sealed class TwoBlockingRuns : IBackupExecutor
    {
        public TaskCompletionSource First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Second { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken FirstToken { get; private set; }
        public CancellationToken SecondToken { get; private set; }
        private int calls;
        public async Task<string> CreateAsync(BackupJob job, string work, CancellationToken token)
        {
            if (Interlocked.Increment(ref calls) == 1) { FirstToken = token; First.SetResult(); }
            else { SecondToken = token; Second.SetResult(); }
            await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException("Unreachable");
        }
    }
    [Fact]
    public async Task CancelCurrentRunKeepsAgentAndNextScheduledJobAliveAndRejectsStaleCommands()
    {
        var first = (await CreateJob()) with { ScheduleKind = ScheduleKind.SystemStartup };
        var second = first with { Id = Guid.NewGuid(), Name = "Следующее задание" };
        await new JobStore(Path.Combine(directory, "jobs.json")).SaveAsync([first, second]);
        var executor = new TwoBlockingRuns(); using var shutdown = new CancellationTokenSource();
        var task = new LocalAgentHost(directory, executor, new NoCredentials()).RunAsync(pollInterval: TimeSpan.FromMilliseconds(50), cancellationToken: shutdown.Token);
        try
        {
            await executor.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var control = new LocalAgentControlStore(directory); var state = (await control.ReadAsync())!;
            var progress = (await new BackupProgressStore(directory).ReadAsync())!;
            Assert.True(state.CanCancelCurrentRun);
            await control.RequestCancelRunAsync(Guid.NewGuid(), progress.RunId);
            await Task.Delay(1200); Assert.False(executor.FirstToken.IsCancellationRequested);
            await control.RequestCancelRunAsync(state.SessionId, Guid.NewGuid());
            await Task.Delay(1200); Assert.False(executor.FirstToken.IsCancellationRequested);
            await control.RequestCancelRunAsync(state.SessionId, progress.RunId);
            await executor.Second.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(executor.FirstToken.IsCancellationRequested);
            Assert.Equal(BackupRunStatus.Cancelled, (await new BackupRunHistory(Path.Combine(directory, "runs.json")).ReadAsync()).Single(run => run.JobId == first.Id).Status);
            // The command remains on disk while the second run is active.
            await Task.Delay(1200); Assert.False(executor.SecondToken.IsCancellationRequested);
            Assert.False(task.IsCompleted); Assert.Equal(LocalAgentPhase.Running, (await control.ReadAsync())!.Phase);
            Assert.True(new BackupEngineCoordinator(directory).IsHeldElsewhere());
        }
        finally { shutdown.Cancel(); await task.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    [Fact]
    public async Task CancelRunCommandRequiresBothIdentifiersAndDoesNotCreateFullStopCommand()
    {
        var control = new LocalAgentControlStore(directory); var session = Guid.NewGuid(); var run = Guid.NewGuid();
        await Assert.ThrowsAsync<ArgumentException>(() => control.RequestCancelRunAsync(Guid.Empty, run));
        await Assert.ThrowsAsync<ArgumentException>(() => control.RequestCancelRunAsync(session, Guid.Empty));
        await control.RequestCancelRunAsync(session, run);
        Assert.Equal(new LocalAgentCancelRunRequest(session, run), await control.ReadCancelRunAsync());
        Assert.False(await control.StopRequestedAsync(session));
    }
    [Fact]
    public async Task ManualOnlyDisabledJobQueuesBehindScheduleWithoutStoppingHostOrReplayingStaleCommands()
    {
        var scheduled = (await CreateJob()) with { ScheduleKind = ScheduleKind.SystemStartup };
        var manual = scheduled with { Id = Guid.NewGuid(), Name = "Ручное без расписания", ScheduleKind = ScheduleKind.Manual, Enabled = false };
        await new JobStore(Path.Combine(directory, "jobs.json")).SaveAsync([scheduled, manual]);
        var executor = new TwoBlockingRuns();
        using var shutdown = new CancellationTokenSource();
        var task = new LocalAgentHost(directory, executor, new NoCredentials()).RunAsync(
            pollInterval: TimeSpan.FromMilliseconds(50), cancellationToken: shutdown.Token);
        var queue = new LocalManualRunQueue(directory);
        LocalManualRunRequest? request = null;
        try
        {
            await executor.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var control = new LocalAgentControlStore(directory);
            var state = (await control.ReadAsync())!;
            Assert.True(state.CanRunManually);
            Assert.True(state.RunsSchedules);
            var stale = await queue.EnqueueAsync(Guid.NewGuid(), manual.Id);
            var missing = await queue.EnqueueAsync(state.SessionId, Guid.NewGuid());
            // A broken command must not prevent subsequent valid requests from being read.
            await File.WriteAllTextAsync(Path.Combine(directory, "manual-runs", "pending", "broken.json"), "{");
            await File.WriteAllTextAsync(Path.Combine(directory, "manual-runs", "pending", "null.json"), "null");
            request = await queue.EnqueueAsync(state.SessionId, manual.Id);
            await WaitForReceipt(queue, request.RequestId, LocalManualRunPhase.Accepted);
            Assert.Equal(LocalManualRunPhase.Rejected, (await queue.ReadReceiptAsync(stale.RequestId))!.Phase);
            Assert.Equal(LocalManualRunPhase.Rejected, (await queue.ReadReceiptAsync(missing.RequestId))!.Phase);
            Assert.False(executor.Second.Task.IsCompleted);
            Assert.False(executor.FirstToken.IsCancellationRequested);
            var progress = (await new BackupProgressStore(directory).ReadAsync())!;
            await control.RequestCancelRunAsync(state.SessionId, progress.RunId);
            await executor.Second.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var runs = await new BackupRunHistory(Path.Combine(directory, "runs.json")).ReadAsync();
            Assert.Equal(BackupRunStatus.Cancelled, runs.Single(r => r.JobId == scheduled.Id).Status);
            Assert.Equal(BackupRunStatus.Running, runs.Single(r => r.JobId == manual.Id).Status);
            Assert.False(task.IsCompleted);
            Assert.Equal(LocalAgentPhase.Running, (await control.ReadAsync())!.Phase);
            Assert.DoesNotContain(await queue.PendingAsync(), r => r.RequestId == request.RequestId);
        }
        finally { shutdown.Cancel(); Assert.Equal(0, await task.WaitAsync(TimeSpan.FromSeconds(5))); }
        Assert.NotNull(request);
        await WaitForReceipt(queue, request.RequestId, LocalManualRunPhase.Interrupted);
        Assert.Equal(2, (await new BackupRunHistory(Path.Combine(directory, "runs.json")).ReadAsync()).Count);
    }

    [Fact]
    public async Task BackgroundManualRunFinishesAndLostAcknowledgementDoesNotDuplicateCopy()
    {
        var job = (await CreateJob()) with { Enabled = false };
        await new JobStore(Path.Combine(directory, "jobs.json")).SaveAsync([job]);
        using var shutdown = new CancellationTokenSource();
        var task = new LocalAgentHost(directory, new DatabaseBackupExecutor(new BackupProcessRunner()), new NoCredentials())
            .RunAsync(pollInterval: TimeSpan.FromMilliseconds(50), cancellationToken: shutdown.Token);
        try
        {
            var state = await WaitForState(new LocalAgentControlStore(directory), s => s.CanRunManually);
            var queue = new LocalManualRunQueue(directory);
            var request = await queue.EnqueueAsync(state.SessionId, job.Id);
            await WaitForReceipt(queue, request.RequestId, LocalManualRunPhase.Finished);
            Assert.Equal(BackupRunStatus.Succeeded, Assert.Single(await new BackupRunHistory(Path.Combine(directory, "runs.json")).ReadAsync()).Status);
            // Simulate a command left behind after completion: its terminal receipt must prevent replay.
            await File.WriteAllTextAsync(Path.Combine(directory, "manual-runs", "pending", request.RequestId.ToString("N") + ".json"),
                System.Text.Json.JsonSerializer.Serialize(request));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while ((await queue.PendingAsync(timeout.Token)).Count != 0) await Task.Delay(25, timeout.Token);
            Assert.Single(await new BackupRunHistory(Path.Combine(directory, "runs.json")).ReadAsync());
            Assert.Single(await new BackupCatalog(Path.Combine(directory, "copies.json")).ReadAsync());
            Assert.False(task.IsCompleted);
        }
        finally { shutdown.Cancel(); Assert.Equal(0, await task.WaitAsync(TimeSpan.FromSeconds(5))); }
    }

    [Fact]
    public async Task UnwritableManualReceiptDoesNotBlockOtherJobsAndRecoversWithoutDuplicates()
    {
        var blocked = (await CreateJob()) with { Enabled = false };
        var other = blocked with { Id = Guid.NewGuid(), Name = "Другое ручное задание" };
        await new JobStore(Path.Combine(directory, "jobs.json")).SaveAsync([blocked, other]);
        using var shutdown = new CancellationTokenSource();
        var task = new LocalAgentHost(directory, new DatabaseBackupExecutor(new BackupProcessRunner()), new NoCredentials())
            .RunAsync(pollInterval: TimeSpan.FromMilliseconds(50), cancellationToken: shutdown.Token);
        try
        {
            var state = await WaitForState(new LocalAgentControlStore(directory), s => s.CanRunManually);
            var queue = new LocalManualRunQueue(directory);
            var first = new LocalManualRunRequest(Guid.NewGuid(), state.SessionId, blocked.Id, DateTimeOffset.UtcNow.AddSeconds(-1));
            var receiptPath = Path.Combine(directory, "manual-runs", "receipts", first.RequestId.ToString("N") + ".json");
            Directory.CreateDirectory(receiptPath); // Deterministic individual write failure on Windows and Linux.
            var pendingPath = Path.Combine(directory, "manual-runs", "pending", first.RequestId.ToString("N") + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(pendingPath)!);
            await File.WriteAllTextAsync(pendingPath, System.Text.Json.JsonSerializer.Serialize(first));
            var second = await queue.EnqueueAsync(state.SessionId, other.Id);
            await WaitForReceipt(queue, second.RequestId, LocalManualRunPhase.Finished);
            var history = new BackupRunHistory(Path.Combine(directory, "runs.json"));
            Assert.Equal(other.Id, Assert.Single(await history.ReadAsync()).JobId);
            Assert.Contains(await queue.PendingAsync(), request => request.RequestId == first.RequestId);
            Assert.False(task.IsCompleted);
            await WaitForState(new LocalAgentControlStore(directory), s => s.Warning?.Contains("Очередь ручных запусков") == true);
            Directory.Delete(receiptPath);
            await WaitForReceipt(queue, first.RequestId, LocalManualRunPhase.Finished);
            var runs = await history.ReadAsync();
            Assert.Equal(2, runs.Count);
            Assert.All(runs, run => Assert.Equal(BackupRunStatus.Succeeded, run.Status));
            await WaitForState(new LocalAgentControlStore(directory), s => s.Warning is null);
        }
        finally { shutdown.Cancel(); Assert.Equal(0, await task.WaitAsync(TimeSpan.FromSeconds(5))); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MismatchedManualReceiptCannotBeReboundOrExecuteACommand(bool differentSession)
    {
        var job = (await CreateJob()) with { Enabled = false };
        await new JobStore(Path.Combine(directory, "jobs.json")).SaveAsync([job]);
        using var shutdown = new CancellationTokenSource();
        var task = new LocalAgentHost(directory, new DatabaseBackupExecutor(new BackupProcessRunner()), new NoCredentials())
            .RunAsync(pollInterval: TimeSpan.FromMilliseconds(50), cancellationToken: shutdown.Token);
        try
        {
            var control = new LocalAgentControlStore(directory);
            var state = await WaitForState(control, s => s.CanRunManually);
            var queue = new LocalManualRunQueue(directory);
            var request = new LocalManualRunRequest(Guid.NewGuid(), state.SessionId, job.Id, DateTimeOffset.UtcNow);
            var existing = new LocalManualRunReceipt(request.RequestId, differentSession ? Guid.NewGuid() : state.SessionId,
                differentSession ? job.Id : Guid.NewGuid(), LocalManualRunPhase.Accepted);
            await queue.WriteReceiptAsync(existing);
            var receiptPath = Path.Combine(directory, "manual-runs", "receipts", request.RequestId.ToString("N") + ".json");
            var originalReceipt = await File.ReadAllBytesAsync(receiptPath);
            var pendingPath = Path.Combine(directory, "manual-runs", "pending", request.RequestId.ToString("N") + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(pendingPath)!);
            await File.WriteAllTextAsync(pendingPath, System.Text.Json.JsonSerializer.Serialize(request));

            await WaitForConsumed(queue, request.RequestId);
            await WaitForState(control, s => s.Warning?.Contains("не совпадает с её подтверждением") == true);
            Assert.Equal(originalReceipt, await File.ReadAllBytesAsync(receiptPath));
            Assert.Empty(await new BackupRunHistory(Path.Combine(directory, "runs.json")).ReadAsync());
            Assert.Empty(await new BackupCatalog(Path.Combine(directory, "copies.json")).ReadAsync());

            var next = await queue.EnqueueAsync(state.SessionId, job.Id);
            await WaitForReceipt(queue, next.RequestId, LocalManualRunPhase.Finished);
            Assert.Equal(BackupRunStatus.Succeeded,
                Assert.Single(await new BackupRunHistory(Path.Combine(directory, "runs.json")).ReadAsync()).Status);
            Assert.Equal(originalReceipt, await File.ReadAllBytesAsync(receiptPath));
            await WaitForState(control, s => s.Warning?.Contains("не совпадает с её подтверждением") != true);
        }
        finally { shutdown.Cancel(); Assert.Equal(0, await task.WaitAsync(TimeSpan.FromSeconds(5))); }
    }

    [Fact]
    public async Task LostFinalReceiptDoesNotBlockNextJobOrRepeatFinishedBackup()
    {
        var firstJob = (await CreateJob()) with { Enabled = false };
        var secondJob = firstJob with { Id = Guid.NewGuid(), Name = "Следующий ручной запуск" };
        await new JobStore(Path.Combine(directory, "jobs.json")).SaveAsync([firstJob, secondJob]);
        var id = Guid.NewGuid();
        var receiptPath = Path.Combine(directory, "manual-runs", "receipts", id.ToString("N") + ".json");
        var blockedReceipt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shutdown = new CancellationTokenSource();
        var task = new LocalAgentHost(directory, new DatabaseBackupExecutor(new BackupProcessRunner()), new NoCredentials())
            .RunAsync(pollInterval: TimeSpan.FromMilliseconds(50), cancellationToken: shutdown.Token, report: run =>
            {
                if (run.JobId == firstJob.Id)
                {
                    File.Delete(receiptPath); Directory.CreateDirectory(receiptPath);
                    blockedReceipt.TrySetResult();
                }
                return Task.CompletedTask;
            });
        try
        {
            var state = await WaitForState(new LocalAgentControlStore(directory), s => s.CanRunManually);
            var queue = new LocalManualRunQueue(directory);
            var pendingPath = Path.Combine(directory, "manual-runs", "pending", id.ToString("N") + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(pendingPath)!);
            var command = System.Text.Json.JsonSerializer.Serialize(new LocalManualRunRequest(id, state.SessionId, firstJob.Id, DateTimeOffset.UtcNow));
            await File.WriteAllTextAsync(pendingPath, command);
            await blockedReceipt.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Redelivery while the terminal receipt is unwritable must retry only the receipt,
            // never the already completed backup. Another request remains independent.
            await File.WriteAllTextAsync(pendingPath, command);
            var second = await queue.EnqueueAsync(state.SessionId, secondJob.Id);
            await WaitForReceipt(queue, second.RequestId, LocalManualRunPhase.Finished);
            Assert.Single(await new BackupRunHistory(Path.Combine(directory, "runs.json")).ReadAsync(), run => run.JobId == firstJob.Id);
            Directory.Delete(receiptPath);
            await WaitForReceipt(queue, id, LocalManualRunPhase.Finished);
            await WaitForConsumed(queue, id);
            var runs = await new BackupRunHistory(Path.Combine(directory, "runs.json")).ReadAsync();
            Assert.Equal(2, runs.Count);
            Assert.All(runs, run => Assert.Equal(BackupRunStatus.Succeeded, run.Status));
            Assert.Single(runs, run => run.JobId == firstJob.Id);
        }
        finally { shutdown.Cancel(); Assert.Equal(0, await task.WaitAsync(TimeSpan.FromSeconds(5))); }
    }

    private static async Task WaitForReceipt(LocalManualRunQueue queue, Guid id, LocalManualRunPhase phase)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while ((await queue.ReadReceiptAsync(id, timeout.Token))?.Phase != phase)
            await Task.Delay(25, timeout.Token);
    }

    private static async Task WaitForConsumed(LocalManualRunQueue queue, Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while ((await queue.PendingAsync(timeout.Token)).Any(request => request.RequestId == id))
            await Task.Delay(25, timeout.Token);
    }

    private sealed class BlockingExecutor : IBackupExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> CreateAsync(BackupJob job, string work, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }
    private sealed class DeniedThenBlockingExecutor(Guid deniedId) : IBackupExecutor
    {
        public TaskCompletionSource NextStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> CreateAsync(BackupJob job, string work, CancellationToken token)
        {
            if (job.Id == deniedId) throw new UnauthorizedAccessException("fixture access denied");
            NextStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable");
        }
    }
    private static async Task<LocalAgentState> WaitForState(LocalAgentControlStore control, Func<LocalAgentState, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var state = await control.ReadAsync(timeout.Token);
            if (state is not null && predicate(state)) return state;
            await Task.Delay(50, timeout.Token);
        }
    }
    [Fact]
    public async Task DeniedBackupShowsWarningWhileAgentExecutesNextJob()
    {
        var first = (await CreateJob()) with { ScheduleKind = ScheduleKind.SystemStartup };
        var second = first with { Id = Guid.NewGuid(), Name = "Следующее задание" };
        await new JobStore(Path.Combine(directory, "jobs.json")).SaveAsync([first, second]);
        var executor = new DeniedThenBlockingExecutor(first.Id);
        using var shutdown = new CancellationTokenSource();
        var task = new LocalAgentHost(directory, executor, new NoCredentials()).RunAsync(
            pollInterval: TimeSpan.FromMilliseconds(50), cancellationToken: shutdown.Token);
        try
        {
            await executor.NextStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var state = await WaitForState(new LocalAgentControlStore(directory), s => s.Warning?.Contains("fixture access denied") == true);
            Assert.Equal(LocalAgentPhase.Running, state.Phase);
            Assert.False(task.IsCompleted);
            var failed = (await new BackupRunHistory(Path.Combine(directory, "runs.json")).ReadAsync()).Single(r => r.JobId == first.Id);
            Assert.Equal(BackupRunStatus.Failed, failed.Status);
            Assert.Null(failed.Backup);
            Assert.Empty(await new BackupCatalog(Path.Combine(directory, "copies.json")).ReadAsync());
        }
        finally { shutdown.Cancel(); Assert.Equal(0, await task.WaitAsync(TimeSpan.FromSeconds(5))); }
    }
    [Fact]
    public async Task UnwritableSettingsWaitForRecoveryWithoutExecutingDefaults()
    {
        var job = (await CreateJob()) with { ScheduleKind = ScheduleKind.SystemStartup };
        await new JobStore(Path.Combine(directory, "jobs.json")).SaveAsync([job]);
        var settingsPath = Path.Combine(directory, "agent.json");
        Directory.CreateDirectory(settingsPath); // Deterministic write failure on both Linux and Windows.
        var executor = new BlockingExecutor(); using var shutdown = new CancellationTokenSource();
        var task = new LocalAgentHost(directory, executor, new NoCredentials()).RunAsync(
            pollInterval: TimeSpan.FromMilliseconds(50), cancellationToken: shutdown.Token);
        try
        {
            var control = new LocalAgentControlStore(directory);
            var state = await WaitForState(control, s => s.Warning?.Contains("Настройки агента") == true);
            Assert.Equal(LocalAgentPhase.Running, state.Phase);
            Assert.False(executor.Started.Task.IsCompleted);
            Assert.False(task.IsCompleted);
            Directory.Delete(settingsPath);
            await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForState(control, s => s.Warning is null);
        }
        finally { shutdown.Cancel(); Assert.Equal(0, await task.WaitAsync(TimeSpan.FromSeconds(5))); }
    }
    [Fact]
    public async Task StateWriteFailureKeepsAgentAliveAndFullStopWorksWithoutWritableState()
    {
        var job = (await CreateJob()) with { ScheduleKind = ScheduleKind.SystemStartup };
        await new JobStore(Path.Combine(directory, "jobs.json")).SaveAsync([job]);
        var statePath = Path.Combine(directory, "local-agent.json");
        Directory.CreateDirectory(statePath);
        var executor = new BlockingExecutor(); using var shutdown = new CancellationTokenSource();
        var task = new LocalAgentHost(directory, executor, new NoCredentials()).RunAsync(
            pollInterval: TimeSpan.FromMilliseconds(50), cancellationToken: shutdown.Token);
        try
        {
            await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(1200);
            Assert.False(task.IsCompleted);
            Directory.Delete(statePath);
            var control = new LocalAgentControlStore(directory);
            // Recovery warning may be cleared by the next heartbeat before a busy CI reader polls.
            // Require a newly writable running state; persistent warning visibility is covered separately.
            var state = await WaitForState(control, s => s.Phase == LocalAgentPhase.Running);
            Assert.Equal(LocalAgentPhase.Running, state.Phase);
            await WaitForState(control, s => s.Warning is null);
            await control.RequestStopAsync(state.SessionId);
            File.Delete(statePath); Directory.CreateDirectory(statePath);
            Assert.Equal(0, await task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(BackupRunStatus.Cancelled, Assert.Single(await new BackupRunHistory(Path.Combine(directory, "runs.json")).ReadAsync()).Status);
            Assert.False(new BackupEngineCoordinator(directory).IsHeldElsewhere());
        }
        finally { shutdown.Cancel(); await task.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    [Fact]
    public async Task UnwritableProgressIsVisibleAsWarningWithoutCancellingTheBackup()
    {
        var job = (await CreateJob()) with { ScheduleKind = ScheduleKind.SystemStartup };
        await new JobStore(Path.Combine(directory, "jobs.json")).SaveAsync([job]);
        Directory.CreateDirectory(Path.Combine(directory, "backup-progress.json"));
        var executor = new BlockingExecutor(); using var shutdown = new CancellationTokenSource();
        var task = new LocalAgentHost(directory, executor, new NoCredentials()).RunAsync(
            pollInterval: TimeSpan.FromMilliseconds(50), cancellationToken: shutdown.Token);
        try
        {
            await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var state = await WaitForState(new LocalAgentControlStore(directory), s => s.Warning?.Contains("Состояние выполнения задания") == true);
            Assert.Equal(LocalAgentPhase.Running, state.Phase); Assert.False(task.IsCompleted);
            Assert.Equal(BackupRunStatus.Running, Assert.Single(await new BackupRunHistory(Path.Combine(directory, "runs.json")).ReadAsync()).Status);
        }
        finally { shutdown.Cancel(); Assert.Equal(0, await task.WaitAsync(TimeSpan.FromSeconds(5))); }
    }
    public void Dispose() => Directory.Delete(directory, true);
}
