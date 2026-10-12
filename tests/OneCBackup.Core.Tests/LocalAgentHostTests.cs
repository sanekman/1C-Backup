using System.Net;
using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

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
    public void Dispose() => Directory.Delete(directory, true);
}
