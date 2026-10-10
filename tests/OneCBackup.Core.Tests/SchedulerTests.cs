using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class SchedulerTests
{
    private static DateTimeOffset At(int day, int hour) => new(2026, 10, day, hour, 0, 0, TimeSpan.Zero);
    private static BackupJob Job => new() { DailyAt = new TimeOnly(23, 0), TimeZoneId = "UTC" };

    [Fact]
    public async Task DispatchesDueSlotExactlyOnce()
    {
        var job = Job;
        var scheduler = new BackupScheduler();
        var count = 0;
        Task Execute(BackupJob _, CancellationToken token) { count++; return Task.CompletedTask; }
        Assert.Empty(await scheduler.RunDueAsync([job], At(10, 22), Execute));
        var result = Assert.Single(await scheduler.RunDueAsync([job], At(10, 23), Execute));
        Assert.Equal(At(10, 23), result.ScheduledAt);
        Assert.Null(result.Error);
        Assert.Empty(await scheduler.RunDueAsync([job], At(10, 23), Execute));
        Assert.Single(await scheduler.RunDueAsync([job], At(11, 23), Execute));
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task ConcurrentTicksDoNotOverlapOrDuplicateExecution()
    {
        var job = Job;
        var scheduler = new BackupScheduler();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        async Task Execute(BackupJob _, CancellationToken token)
        { count++; entered.SetResult(); await release.Task.WaitAsync(token); }
        var first = scheduler.RunDueAsync([job], At(10, 23), Execute);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = scheduler.RunDueAsync([job], At(10, 23), Execute);
        release.SetResult();
        Assert.Single(await first);
        Assert.Empty(await second);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task FailureDoesNotBlockOtherJobsOrRetrySameSlot()
    {
        var first = Job;
        var second = Job;
        var scheduler = new BackupScheduler();
        Task Execute(BackupJob job, CancellationToken _) => job.Id == first.Id
            ? Task.FromException(new IOException("fixture failure")) : Task.CompletedTask;
        var results = await scheduler.RunDueAsync([first, second], At(10, 23), Execute);
        Assert.Equal(2, results.Count);
        Assert.IsType<IOException>(results[0].Error);
        Assert.Null(results[1].Error);
        Assert.Empty(await scheduler.RunDueAsync([first, second], At(10, 23), Execute));
    }

    [Fact]
    public async Task DelayedTickRunsOnceWithoutBacklog()
    {
        var job = Job;
        var scheduler = new BackupScheduler();
        Task Execute(BackupJob _, CancellationToken token) => Task.CompletedTask;
        await scheduler.RunDueAsync([job], At(9, 22), Execute);
        Assert.Single(await scheduler.RunDueAsync([job], At(12, 10), Execute));
        Assert.Empty(await scheduler.RunDueAsync([job], At(12, 10), Execute));
    }

    [Fact]
    public async Task RestartStartsWithFutureSlotAndManualJobsAreIgnored()
    {
        Task Execute(BackupJob _, CancellationToken token) => throw new InvalidOperationException("must not execute");
        Assert.Empty(await new BackupScheduler().RunDueAsync([Job], At(10, 23).AddMinutes(1), Execute));
        Assert.Empty(await new BackupScheduler().RunDueAsync([Job with { ScheduleKind = ScheduleKind.Manual },
            Job with { Enabled = false }], At(10, 23), Execute));
    }

    [Fact]
    public async Task ChangedScheduleIsRecalculated()
    {
        var job = Job;
        var scheduler = new BackupScheduler();
        Task Execute(BackupJob _, CancellationToken token) => Task.CompletedTask;
        await scheduler.RunDueAsync([job], At(10, 14), Execute);
        Assert.Single(await scheduler.RunDueAsync([job with { DailyAt = new TimeOnly(15, 0) }], At(10, 15), Execute));
    }

    [Fact]
    public async Task TimerHostExecutesAndStopsOnCancellation()
    {
        var job = Job;
        var scheduler = new BackupScheduler();
        using var cancellation = new CancellationTokenSource();
        var count = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.RunAsync(
            _ => Task.FromResult<IReadOnlyList<BackupJob>>([job]),
            (_, _) => { count++; return Task.CompletedTask; },
            _ => { cancellation.Cancel(); return Task.CompletedTask; },
            timeProvider: new FixedClock(At(10, 23)), cancellationToken: cancellation.Token));
        Assert.Equal(1, count);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => now; }
}
