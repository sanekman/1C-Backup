using System.Text.Json.Nodes;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class ScheduleTests
{
    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    private static BackupJob Daily => new() { DailyAt = new TimeOnly(23, 0), TimeZoneId = "UTC" };

    [Theory]
    [InlineData("2026-10-10T22:00:00Z", "2026-10-10T23:00:00Z")]
    [InlineData("2026-10-10T23:00:00Z", "2026-10-11T23:00:00Z")]
    [InlineData("2026-10-10T23:10:00Z", "2026-10-11T23:00:00Z")]
    public void DailyUsesNextOccurrence(string after, string expected) =>
        Assert.Equal(At(expected), BackupSchedule.NextRun(Daily, At(after)));

    [Fact]
    public void UsesChosenTimeZone()
    {
        Assert.Equal(At("2026-10-10T13:00:00Z"), BackupSchedule.NextRun(
            Daily with { TimeZoneId = "Asia/Vladivostok" }, At("2026-10-10T09:00:00Z")));
    }

    [Theory]
    [InlineData("2026-10-09T23:01:00Z", "2026-10-12T23:00:00Z")]
    [InlineData("2026-10-12T22:00:00Z", "2026-10-12T23:00:00Z")]
    public void WeeklySkipsUnselectedDays(string after, string expected)
    {
        var job = Daily with { ScheduleKind = ScheduleKind.Weekly, DaysOfWeek = WeekDays.Monday | WeekDays.Friday };
        Assert.Equal(At(expected), BackupSchedule.NextRun(job, At(after)));
    }

    [Fact]
    public void SundayIsSupported()
    {
        Assert.Equal(At("2026-10-11T23:00:00Z"), BackupSchedule.NextRun(Daily with
        { ScheduleKind = ScheduleKind.Weekly, DaysOfWeek = WeekDays.Sunday }, At("2026-10-10T23:00:00Z")));
    }

    [Fact]
    public void DisabledAndManualJobsHaveNoAutomaticRun()
    {
        Assert.Null(BackupSchedule.NextRun(Daily with { Enabled = false }, At("2026-10-10T10:00:00Z")));
        Assert.Null(BackupSchedule.NextRun(Daily with { ScheduleKind = ScheduleKind.Manual }, At("2026-10-10T10:00:00Z")));
    }

    [Fact]
    public void DstGapMovesToFirstValidMinute()
    {
        var job = Daily with { TimeZoneId = "America/New_York", DailyAt = new TimeOnly(2, 30) };
        Assert.Equal(At("2026-03-08T07:00:00Z"), BackupSchedule.NextRun(job, At("2026-03-08T06:00:00Z")));
    }

    [Fact]
    public void RepeatedHourUsesFirstOccurrenceOnly()
    {
        var job = Daily with { TimeZoneId = "America/New_York", DailyAt = new TimeOnly(1, 30) };
        Assert.Equal(At("2026-11-01T05:30:00Z"), BackupSchedule.NextRun(job, At("2026-11-01T04:00:00Z")));
        Assert.Equal(At("2026-11-02T06:30:00Z"), BackupSchedule.NextRun(job, At("2026-11-01T05:45:00Z")));
    }

    [Theory]
    [InlineData(WeekDays.None)]
    [InlineData((WeekDays)128)]
    public void WeeklyNeedsValidDays(WeekDays days)
    {
        Assert.Throws<ArgumentException>(() => BackupSchedule.NextRun(Daily with
        { ScheduleKind = ScheduleKind.Weekly, DaysOfWeek = days }, At("2026-10-10T10:00:00Z")));
    }

    [Fact]
    public async Task ScheduleAndRetentionRoundTripAndLegacyRemainsDaily()
    {
        var directory = Path.Combine(Path.GetTempPath(), "OneCBackup-schedule-" + Guid.NewGuid());
        var path = Path.Combine(directory, "jobs.json");
        try
        {
            var job = Daily with { Name = "Test", Source = "db", DestinationDirectory = directory,
                ScheduleKind = ScheduleKind.Weekly, DaysOfWeek = WeekDays.Tuesday | WeekDays.Saturday, KeepLast = 3 };
            await new JobStore(path).SaveAsync([job]);
            Assert.Equal(job, Assert.Single(await new JobStore(path).LoadAsync()));
            var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            document[0]!.AsObject().Remove("ScheduleKind");
            document[0]!.AsObject().Remove("DaysOfWeek");
            await File.WriteAllTextAsync(path, document.ToJsonString());
            var legacy = Assert.Single(await new JobStore(path).LoadAsync());
            Assert.Equal(ScheduleKind.Daily, legacy.ScheduleKind);
            Assert.Equal(job.DailyAt, legacy.DailyAt);
            Assert.Equal(3, legacy.KeepLast);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
