using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class LocalDispatcherTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OneCBackup-dispatcher-" + Guid.NewGuid());
    private JobStore Store => new(Path.Combine(root, "jobs.json"));
    private static DateTimeOffset Now => new(2026, 10, 11, 13, 0, 0, TimeSpan.Zero);
    private BackupJob Job => new()
    {
        Name = "File archive", Source = Path.Combine(root, "database"), DestinationDirectory = Path.Combine(root, "copies"),
        OneCMethod = OneCBackupMethod.FileArchive, DailyAt = new TimeOnly(13, 0), TimeZoneId = "UTC"
    };

    [Fact]
    public async Task ManualAndScheduledRunsShareGate()
    {
        var job = Job;
        await Store.SaveAsync([job]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var dispatcher = new LocalBackupDispatcher(Store, async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.SetResult(); await release.Task.WaitAsync(token); }
        });
        var manual = dispatcher.ExecuteAsync(job);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var schedule = dispatcher.RunScheduleAsync(_ => { cancellation.Cancel(); return Task.CompletedTask; },
            cancellation.Token, timeProvider: new FixedClock(Now));
        Assert.Equal(1, Volatile.Read(ref calls));
        release.SetResult();
        await manual;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => schedule);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CancellingQueuedRunDoesNotStartItAndGateRemainsUsable()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var dispatcher = new LocalBackupDispatcher(Store, async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.SetResult(); await release.Task.WaitAsync(token); }
        });
        var first = dispatcher.ExecuteAsync(Job);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var queued = dispatcher.ExecuteAsync(Job, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        release.SetResult(); await first;
        await dispatcher.ExecuteAsync(Job);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task OnlyOneScheduleLoopCanRunAndStopReleasesIt()
    {
        await Store.SaveAsync([Job]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new LocalBackupDispatcher(Store, async (_, token) =>
        { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); });
        using var cancellation = new CancellationTokenSource();
        var first = dispatcher.RunScheduleAsync(_ => Task.CompletedTask, cancellation.Token, timeProvider: new FixedClock(Now));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.RunScheduleAsync(_ => Task.CompletedTask));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        using var stopped = new CancellationTokenSource(); stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.RunScheduleAsync(_ => Task.CompletedTask, stopped.Token));
    }

    [Fact]
    public async Task RestartDoesNotReplayConsumedSlot()
    {
        await Store.SaveAsync([Job]);
        var calls = 0;
        var dispatcher = new LocalBackupDispatcher(Store, (_, _) => { calls++; return Task.CompletedTask; });
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.RunScheduleAsync(
            _ => { cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token, timeProvider: new FixedClock(Now)));
        Assert.Equal(1, calls);
        using var secondCancellation = new CancellationTokenSource();
        var observedRead = new ReadCancellingClock(Now, secondCancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.RunScheduleAsync(
            _ => Task.CompletedTask, secondCancellation.Token, pollInterval: TimeSpan.FromMilliseconds(10), timeProvider: observedRead));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ReadsUpdatedSavedJobsAndAcceptsFtpSchedules()
    {
        var dispatcher = new LocalBackupDispatcher(Store, (_, _) => Task.CompletedTask);
        var job = Job;
        await Store.SaveAsync([job]);
        Assert.Equal(job, Assert.Single(await dispatcher.LoadScheduledJobsAsync()));
        await Store.SaveAsync([job with { Enabled = false }]);
        Assert.False(Assert.Single(await dispatcher.LoadScheduledJobsAsync()).Enabled);
        await Store.SaveAsync([job with { StorageKind = StorageKind.Ftp, DestinationDirectory = "/copies",
            Ftp = new FtpSettings { Host = "ftp.example.com", CredentialKey = "fixture" } }]);
        Assert.Equal(StorageKind.Ftp, Assert.Single(await dispatcher.LoadScheduledJobsAsync()).StorageKind);
        await Store.SaveAsync([job with { Kind = DatabaseKind.SqlServer, ScheduleKind = ScheduleKind.Manual }]);
        Assert.Single(await dispatcher.LoadScheduledJobsAsync());
    }

    [Fact]
    public async Task ScheduleReachesRealFileArchiveRunnerAndPersistentHistory()
    {
        var job = Job;
        Directory.CreateDirectory(job.Source);
        await File.WriteAllTextAsync(Path.Combine(job.Source, "1Cv8.1CD"), "closed database fixture");
        await Store.SaveAsync([job]);
        var history = new BackupRunHistory(Path.Combine(root, "runs.json"));
        var runner = new LocalBackupRunner(new OneCFileArchiveExecutor(),
            new BackupCompletion(new FileBackupStorage(), new BackupCatalog(Path.Combine(root, "catalog.json"))),
            history, Path.Combine(root, "work"));
        var dispatcher = new LocalBackupDispatcher(Store, async (snapshot, token) => { await runner.RunAsync(snapshot, token); });
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.RunScheduleAsync(
            _ => { cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token, timeProvider: new FixedClock(Now)));
        var result = Assert.Single(await history.ReadAsync());
        Assert.Equal(BackupRunStatus.Succeeded, result.Status);
        Assert.True(File.Exists(result.Backup!.Location));
        Assert.EndsWith(".1CD.zip", result.Backup.Location);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class ReadCancellingClock(DateTimeOffset now, CancellationTokenSource cancellation) : TimeProvider
    {
        private int reads;
        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Increment(ref reads) == 2) cancellation.Cancel();
            return now;
        }
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
