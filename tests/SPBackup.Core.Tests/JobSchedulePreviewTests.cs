using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class JobSchedulePreviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 8, 0, 0, TimeSpan.Zero);
    private static BackupJob Job => new() { Name = "Preview", TimeZoneId = "UTC", Enabled = true,
        ScheduleKind = ScheduleKind.Daily, DailyAt = new(9, 0), AdditionalDailyTimes = [new(18, 0)] };

    [Fact]
    public void PreviewKeepsDistinctTimesAcrossDaysWithoutChangingJob()
    {
        var job = Job;
        var result = JobSchedulePreview.Create(job, Now, false);
        Assert.Null(result.Error);
        Assert.Equal(new[] { Now.AddHours(1), Now.AddHours(10), Now.AddDays(1).AddHours(1) }, result.Runs);
        Assert.Equal("UTC", job.TimeZoneId); Assert.Equal("UTC", result.TimeZoneId);
    }
    [Fact]
    public void DisabledAndManualJobsDoNotInventPlannedRuns()
    {
        var disabled = JobSchedulePreview.Create(Job with { Enabled = false }, Now, false);
        Assert.Empty(disabled.Runs); Assert.Contains("выключены", disabled.Summary);
        var manual = JobSchedulePreview.Create(Job with { ScheduleKind = ScheduleKind.Manual }, Now, false);
        Assert.Empty(manual.Runs); Assert.Contains("вручную", manual.Summary);
    }
    [Fact]
    public void StartupDoesNotPretendToHaveAnExactTime()
    {
        var result = JobSchedulePreview.Create(Job with { ScheduleKind = ScheduleKind.SystemStartup }, Now, false);
        Assert.Empty(result.Runs); Assert.Null(result.Error); Assert.Contains("Одна попытка", result.Summary);
    }
    [Fact]
    public void InvalidZoneIsReportedAndStandaloneUsesLocalWithoutMutatingSavedZone()
    {
        var job = Job with { TimeZoneId = "Invalid/TimeZone" };
        Assert.NotNull(JobSchedulePreview.Create(job, Now, false).Error);
        var local = JobSchedulePreview.Create(job, Now, true);
        Assert.Null(local.Error); Assert.Equal(3, local.Runs.Count);
        Assert.Equal(BackupSchedule.LocalTimeZone, local.TimeZoneId);
        Assert.Equal("Invalid/TimeZone", job.TimeZoneId);
    }
    [Fact]
    public void WeeklyPreviewSkipsDaysOutsideThePlan()
    {
        var result = JobSchedulePreview.Create(Job with { ScheduleKind = ScheduleKind.Weekly, DaysOfWeek = WeekDays.Monday, AdditionalDailyTimes = [] }, Now, false);
        Assert.Equal(3, result.Runs.Count);
        Assert.All(result.Runs, run => Assert.Equal(DayOfWeek.Monday, run.UtcDateTime.DayOfWeek));
        Assert.Equal(TimeSpan.FromDays(7), result.Runs[1] - result.Runs[0]);
    }
}
