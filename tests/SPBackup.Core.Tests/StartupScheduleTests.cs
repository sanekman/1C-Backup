using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class StartupScheduleTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "startup-schedule-" + Guid.NewGuid());
    private readonly Guid boot = Guid.NewGuid();
    private string StatePath => Path.Combine(root, "startup-schedule.json");
    private static BackupJob Job => new() { Name = "Startup", Source = "database", DestinationDirectory = "copies",
        OneCMethod = OneCBackupMethod.FileArchive, ScheduleKind = ScheduleKind.SystemStartup };
    private BackupScheduler Scheduler(Func<Guid>? identity = null) => new(new StartupBackupSchedule(StatePath, identity ?? (() => boot)));

    [Fact]
    public async Task OncePerBootAcrossClockChangesAndNewSchedulerInstances()
    {
        var job = Job;
        var count = 0;
        Task Execute(BackupJob _, CancellationToken token) { count++; return Task.CompletedTask; }
        var now = DateTimeOffset.UtcNow;
        Assert.Single(await Scheduler().RunDueAsync([job], now, Execute));
        Assert.Empty(await Scheduler().RunDueAsync([job], now.AddDays(-10), Execute));
        Assert.Empty(await Scheduler().RunDueAsync([job], now.AddDays(10), Execute));
        Assert.Single(await Scheduler(Guid.NewGuid).RunDueAsync([job], now, Execute));
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task DisabledAndManualJobsDoNotConsumeStartupAttempt()
    {
        var job = Job;
        var scheduler = Scheduler();
        Task Execute(BackupJob _, CancellationToken token) => Task.CompletedTask;
        Assert.Empty(await scheduler.RunDueAsync([job with { Enabled = false }], DateTimeOffset.UtcNow, Execute));
        Assert.False(File.Exists(StatePath));
        Assert.Empty(await scheduler.RunDueAsync([job with { ScheduleKind = ScheduleKind.Manual }], DateTimeOffset.UtcNow, Execute));
        Assert.Single(await scheduler.RunDueAsync([job], DateTimeOffset.UtcNow, Execute));
        Assert.Empty(await Scheduler().RunDueAsync([job with { Enabled = true, Name = "Renamed" }], DateTimeOffset.UtcNow, Execute));
    }

    [Fact]
    public async Task FailedAttemptIsNotRepeatedAndOtherJobsStillRun()
    {
        var first = Job; var second = Job;
        Task Execute(BackupJob job, CancellationToken token) => job.Id == first.Id
            ? Task.FromException(new IOException("source unavailable")) : Task.CompletedTask;
        var results = await Scheduler().RunDueAsync([first, second], DateTimeOffset.UtcNow, Execute);
        Assert.Equal(2, results.Count); Assert.IsType<IOException>(results[0].Error); Assert.Null(results[1].Error);
        Assert.Empty(await Scheduler().RunDueAsync([first, second], DateTimeOffset.UtcNow, Execute));
    }

    [Fact]
    public async Task ConcurrentTicksDoNotDuplicateStartupJob()
    {
        var scheduler = Scheduler(); var job = Job; var count = 0;
        Task Execute(BackupJob _, CancellationToken token) { count++; return Task.CompletedTask; }
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => scheduler.RunDueAsync([job], DateTimeOffset.UtcNow, Execute)));
        Assert.Single(results.SelectMany(result => result)); Assert.Equal(1, count);
    }

    [Fact]
    public async Task CancellationBeforeDispatchDoesNotConsumeAttempt()
    {
        var scheduler = Scheduler(); var job = Job;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Task Execute(BackupJob _, CancellationToken token) => Task.CompletedTask;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.RunDueAsync([job], DateTimeOffset.UtcNow, Execute, cancellation.Token));
        Assert.False(File.Exists(StatePath));
        Assert.Single(await scheduler.RunDueAsync([job], DateTimeOffset.UtcNow, Execute));
    }

    [Fact]
    public async Task CancellationDuringExecutionIsNotReplayedAfterAgentRestart()
    {
        var job = Job;
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scheduler().RunDueAsync([job], DateTimeOffset.UtcNow,
            (_, token) => { cancellation.Cancel(); return Task.FromCanceled(token); }, cancellation.Token));
        Assert.Empty(await Scheduler().RunDueAsync([job], DateTimeOffset.UtcNow,
            (_, _) => throw new InvalidOperationException("Cancelled attempt must not replay")));
    }

    [Fact]
    public async Task StartupAndDailySchedulesBothDispatchOnTheSameTick()
    {
        var now = new DateTimeOffset(2026, 10, 12, 13, 0, 0, TimeSpan.Zero);
        var startup = Job; var daily = Job with { ScheduleKind = ScheduleKind.Daily, DailyAt = new(13, 0) };
        var results = await Scheduler().RunDueAsync([startup, daily], now, (_, _) => Task.CompletedTask);
        Assert.Equal(2, results.Count);
        Assert.Equal(new[] { startup.Id, daily.Id }, results.Select(result => result.JobId));
    }

    [Fact]
    public async Task CorruptStateStopsDispatchInsteadOfRepeatingCopies()
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(StatePath, "{\"BootId\":\"" + boot + "\",\"AttemptedJobs\":null}");
        var executed = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => Scheduler().RunDueAsync([Job], DateTimeOffset.UtcNow,
            (_, _) => { executed = true; return Task.CompletedTask; }));
        Assert.False(executed);
    }

    [Fact]
    public async Task StartupScheduleIsPersistedAndHasNoCalendarNextRun()
    {
        var job = Job; var store = new JobStore(Path.Combine(root, "jobs.json"));
        await store.SaveAsync([job]);
        var saved = Assert.Single(await store.LoadAsync());
        Assert.Equal(ScheduleKind.SystemStartup, saved.ScheduleKind);
        Assert.Null(BackupSchedule.NextRun(saved, DateTimeOffset.UtcNow));
        Assert.Equal("При запуске системы", saved.ScheduleDescription);
    }

    [Fact]
    public void NativeBootIdentityIsNonemptyAndStable()
    {
        Assert.NotEqual(Guid.Empty, SystemBootSession.ReadId());
        Assert.Equal(SystemBootSession.ReadId(), SystemBootSession.ReadId());
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
