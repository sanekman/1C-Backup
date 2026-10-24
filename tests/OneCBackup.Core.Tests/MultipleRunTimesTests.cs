using System.Text.Json;
using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class MultipleRunTimesTests
{
    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    private static BackupJob Job => new() { DailyAt = new TimeOnly(23, 0),
        AdditionalDailyTimes = [new(13, 0), new(9, 0)], TimeZoneId = "UTC" };

    [Theory]
    [InlineData("2026-10-10T08:59:59Z", "2026-10-10T09:00:00Z")]
    [InlineData("2026-10-10T09:00:00Z", "2026-10-10T13:00:00Z")]
    [InlineData("2026-10-10T13:00:00Z", "2026-10-10T23:00:00Z")]
    [InlineData("2026-10-10T23:00:00Z", "2026-10-11T09:00:00Z")]
    public void UsesAllTimesInOrderAndSkipsConsumedInstant(string after, string expected) =>
        Assert.Equal(At(expected), BackupSchedule.NextRun(Job, At(after)));

    [Fact]
    public void WeeklyAndTimeZoneApplyToEveryTime()
    {
        var job = Job with { ScheduleKind = ScheduleKind.Weekly, DaysOfWeek = WeekDays.Monday,
            TimeZoneId = "Asia/Vladivostok" };
        Assert.Equal(At("2026-10-11T23:00:00Z"), BackupSchedule.NextRun(job, At("2026-10-10T14:00:00Z")));
        Assert.Equal(At("2026-10-12T03:00:00Z"), BackupSchedule.NextRun(job, At("2026-10-11T23:00:00Z")));
    }

    [Fact]
    public void SpringGapCoalescesTimesIntoOneInstant()
    {
        var job = Job with { DailyAt = new TimeOnly(2, 15), AdditionalDailyTimes = [new(2, 45), new(3, 0), new(4, 0)],
            TimeZoneId = "America/New_York" };
        Assert.Equal(At("2026-03-08T07:00:00Z"), BackupSchedule.NextRun(job, At("2026-03-08T06:00:00Z")));
        Assert.Equal(At("2026-03-08T08:00:00Z"), BackupSchedule.NextRun(job, At("2026-03-08T07:00:00Z")));
    }

    [Fact]
    public void RepeatedHourDoesNotRepeatAnyTime()
    {
        var job = Job with { DailyAt = new TimeOnly(1, 15), AdditionalDailyTimes = [new(1, 45)],
            TimeZoneId = "America/New_York" };
        Assert.Equal(At("2026-11-01T05:45:00Z"), BackupSchedule.NextRun(job, At("2026-11-01T05:30:00Z")));
        Assert.Equal(At("2026-11-02T06:15:00Z"), BackupSchedule.NextRun(job, At("2026-11-01T06:00:00Z")));
    }

    [Fact]
    public void DuplicateOrTooManyTimesAreRejected()
    {
        Assert.NotEmpty((Job with { AdditionalDailyTimes = [new(23, 0)] }).ValidateSchedule());
        Assert.NotEmpty((Job with { AdditionalDailyTimes = [new(9, 0), new(9, 0)] }).ValidateSchedule());
        Assert.NotEmpty((Job with { AdditionalDailyTimes = Enumerable.Range(0, 48).Select(i => new TimeOnly(0, i)).ToArray() }).ValidateSchedule());
        Assert.Throws<ArgumentException>(() => BackupSchedule.NextRun(Job with { AdditionalDailyTimes = [new(23, 0)] }, At("2026-10-10T00:00:00Z")));
    }

    [Fact]
    public void NewConfigurationRoundTripsAndLegacyKeepsItsTime()
    {
        var restored = JsonSerializer.Deserialize<BackupJob>(JsonSerializer.Serialize(Job))!;
        Assert.Equal(Job.ScheduledTimes, restored.ScheduledTimes);
        var legacy = JsonSerializer.Deserialize<BackupJob>("{\"Id\":\"12345678-1234-1234-1234-123456789abc\",\"DailyAt\":\"22:15:00\"}")!;
        Assert.Null(legacy.AdditionalDailyTimes);
        Assert.Equal(new TimeOnly(22, 15), Assert.Single(legacy.ScheduledTimes));
    }

    [Fact]
    public async Task DispatcherConsumesEachTimeOnceAndAppliesScheduleChanges()
    {
        var scheduler = new BackupScheduler();
        var job = Job;
        Task Execute(BackupJob _, CancellationToken token) => Task.CompletedTask;
        await scheduler.RunDueAsync([job], At("2026-10-10T08:00:00Z"), Execute);
        Assert.Single(await scheduler.RunDueAsync([job], At("2026-10-10T09:00:00Z"), Execute));
        Assert.Empty(await scheduler.RunDueAsync([job], At("2026-10-10T09:00:00Z"), Execute));
        Assert.Single(await scheduler.RunDueAsync([job], At("2026-10-10T13:00:00Z"), Execute));
        var changed = job with { AdditionalDailyTimes = [new(14, 0)] };
        Assert.Single(await scheduler.RunDueAsync([changed], At("2026-10-10T14:00:00Z"), Execute));
        Assert.Empty(await scheduler.RunDueAsync([changed], At("2026-10-10T14:00:00Z"), Execute));
    }
}
