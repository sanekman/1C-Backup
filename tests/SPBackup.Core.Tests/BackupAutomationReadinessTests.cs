using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class BackupAutomationReadinessTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static LocalAgentState Host(bool scheduler = true, int age = 0, LocalAgentPhase phase = LocalAgentPhase.Running) =>
        new(Guid.NewGuid(), 123, Now.AddMinutes(-2), Now.AddSeconds(-age), phase) { CanRunManually = scheduler, RunsSchedules = scheduler };
    private static BackupJob Job(string name, ScheduleKind kind = ScheduleKind.Daily) => new() {
        Name = name, ScheduleKind = kind, DailyAt = new TimeOnly(23, 0), TimeZoneId = "UTC" };

    [Theory]
    [InlineData(true, AutomationExecution.Background)]
    [InlineData(false, AutomationExecution.OneRun)]
    public void AManualProcessDoesNotClaimThatSchedulesWillRun(bool scheduler, AutomationExecution expected)
    {
        var result = BackupAutomationReadiness.Evaluate([Job("Daily")], new(Now, EngineBusy: true, Agent: Host(scheduler)));
        Assert.Equal(expected, result.Execution); Assert.Equal(scheduler, result.ScheduleRunning);
    }

    [Theory]
    [InlineData(10)] [InlineData(60)] [InlineData(-3)]
    public void StaleOrFutureHeartbeatDoesNotReportReadiness(int age)
    {
        var result = BackupAutomationReadiness.Evaluate([], new(Now, EngineBusy: true, Agent: Host(age: age), Service: AutomationService.Running));
        Assert.Equal(AutomationExecution.Busy, result.Execution); Assert.False(result.ScheduleRunning);
    }

    [Fact]
    public void OldRunningStateWithoutAnEngineLockCannotClaimReadiness()
    {
        var result = BackupAutomationReadiness.Evaluate([], new(Now, Agent: Host()));
        Assert.Equal(AutomationExecution.Off, result.Execution);
        Assert.Equal(AutomationExecution.ServiceNotReady, BackupAutomationReadiness.Evaluate([], new(Now, Agent: Host(), Service: AutomationService.Running)).Execution);
    }

    [Fact]
    public void ServiceReadinessRequiresBothServiceAndActualScheduler()
    {
        var service = BackupAutomationReadiness.Evaluate([], new(Now, EngineBusy: true, Agent: Host(), Service: AutomationService.Running));
        Assert.Equal(AutomationExecution.Service, service.Execution);
        Assert.Equal(AutomationExecution.Background, BackupAutomationReadiness.Evaluate([], new(Now, EngineBusy: true, Agent: Host(), Service: AutomationService.OtherProfile)).Execution);
        Assert.Equal(AutomationExecution.OneRun, BackupAutomationReadiness.Evaluate([], new(Now, EngineBusy: true, Agent: Host(false), Service: AutomationService.Running)).Execution);
    }

    [Fact]
    public void StoppingAndUnreadableStateCannotKeepAGreenScheduleStatus()
    {
        Assert.Equal(AutomationExecution.Stopping, BackupAutomationReadiness.Evaluate([], new(Now, WindowScheduler: true, WindowStopping: true)).Execution);
        Assert.Equal(AutomationExecution.Unavailable, BackupAutomationReadiness.Evaluate([], new(Now, EngineBusy: true, Agent: Host(), ReadFailed: true)).Execution);
        Assert.Equal(AutomationExecution.Stopping, BackupAutomationReadiness.Evaluate([], new(Now, EngineBusy: true, Agent: Host(phase: LocalAgentPhase.Stopping))).Execution);
    }

    [Fact]
    public void WindowSchedulerIsExplicitlyDifferentFromBackgroundAndService()
    {
        var result = BackupAutomationReadiness.Evaluate([], new(Now, WindowScheduler: true));
        Assert.Equal(AutomationExecution.Window, result.Execution); Assert.True(result.ScheduleRunning);
    }

    [Fact]
    public void PlanSeparatesManualDisabledStartupInvalidAndTimedJobs()
    {
        var daily = Job("daily"); var earlier = Job("earlier") with { DailyAt = new TimeOnly(22, 0) };
        var jobs = new[] { daily, earlier, Job("manual", ScheduleKind.Manual), Job("startup", ScheduleKind.SystemStartup),
            Job("disabled") with { Enabled = false }, Job("invalid", ScheduleKind.Weekly) with { DaysOfWeek = WeekDays.None } };
        var result = BackupAutomationReadiness.Evaluate(jobs, new(Now));
        Assert.Equal(2, result.Plan.TimedJobs); Assert.Equal(1, result.Plan.StartupJobs);
        Assert.Equal(1, result.Plan.DisabledJobs); Assert.Equal(1, result.Plan.InvalidJobs);
        var expected = jobs.Where(job => job.Name is "daily" or "earlier")
            .Select(job => (job, next: BackupSchedule.NextRun(job with { TimeZoneId = BackupSchedule.LocalTimeZone }, Now)))
            .OrderBy(item => item.next).First();
        Assert.Equal(expected.job.Id, result.Plan.NextJobId); Assert.Equal(expected.next, result.Plan.NextRunUtc);
        Assert.Equal("UTC", daily.TimeZoneId); // Status must not mutate stored settings.
    }

    [Fact]
    public void LegacyStateIsAcceptedWithoutInventingUnsupportedHostCapabilities()
    {
        Assert.Equal(AutomationExecution.Background, BackupAutomationReadiness.Evaluate([], new(Now, true, Host() with { RunsSchedules = null })).Execution);
        Assert.Equal(AutomationExecution.Busy, BackupAutomationReadiness.Evaluate([], new(Now, true, Host(false) with { RunsSchedules = null })).Execution);
        Assert.Equal(AutomationExecution.Background, BackupAutomationReadiness.Evaluate([], new(Now, true, Host() with { CanRunManually = false })).Execution);
    }

    [Fact]
    public void LocalManualRunIsNotASchedulerButDoesNotDisableAnExistingWindowScheduler()
    {
        Assert.Equal(AutomationExecution.OneRun, BackupAutomationReadiness.Evaluate([], new(Now, ManualRun: true)).Execution);
        Assert.Equal(AutomationExecution.Window, BackupAutomationReadiness.Evaluate([], new(Now, WindowScheduler: true, ManualRun: true)).Execution);
    }

    [Fact]
    public void StartupJobsNeverInventANextClockTime()
    {
        var result = BackupAutomationReadiness.Evaluate([Job("startup", ScheduleKind.SystemStartup)], new(Now));
        Assert.Equal(1, result.Plan.StartupJobs); Assert.Null(result.Plan.NextRunUtc);
    }
}
