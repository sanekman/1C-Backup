namespace SPBackup.Core;

public enum AutomationExecution { Off, Starting, Stopping, Window, Background, Service, OneRun, Busy, ServiceNotReady, Faulted, Unavailable }
public enum AutomationService { None, Stopped, Starting, Running, Stopping, OtherProfile, Unavailable }
public sealed record AutomationObservation(DateTimeOffset Now, bool EngineBusy = false, LocalAgentState? Agent = null,
    bool WindowScheduler = false, bool WindowStopping = false, bool Starting = false,
    AutomationService Service = AutomationService.None, bool ReadFailed = false, bool ManualRun = false);
public sealed record AutomaticBackupPlan(int TimedJobs, int StartupJobs, int DisabledJobs, int InvalidJobs,
    Guid? NextJobId, string? NextJobName, DateTimeOffset? NextRunUtc);
public sealed record BackupAutomationReadiness(AutomationExecution Execution, AutomaticBackupPlan Plan)
{
    public bool ScheduleRunning => Execution is AutomationExecution.Window or AutomationExecution.Background or AutomationExecution.Service;

    public static BackupAutomationReadiness Evaluate(IReadOnlyList<BackupJob> jobs, AutomationObservation observation)
    {
        var timed = 0; var startup = 0; var disabled = 0; var invalid = 0;
        BackupJob? nextJob = null; DateTimeOffset? nextRun = null;
        foreach (var job in jobs)
        {
            if (job.ScheduleKind == ScheduleKind.Manual) continue;
            if (!job.Enabled) { disabled++; continue; }
            try
            {
                // The standalone dispatcher always uses the current Windows time zone.
                var local = job with { TimeZoneId = BackupSchedule.LocalTimeZone };
                if (local.ValidateSchedule().Count != 0) { invalid++; continue; }
                if (local.ScheduleKind == ScheduleKind.SystemStartup) { startup++; continue; }
                var next = BackupSchedule.NextRun(local, observation.Now);
                timed++;
                if (next is not null && (nextRun is null || next < nextRun)) { nextRun = next; nextJob = job; }
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or TimeZoneNotFoundException or InvalidTimeZoneException)
            { invalid++; }
        }
        var execution = DetermineExecution(observation);
        return new(execution, new(timed, startup, disabled, invalid, nextJob?.Id, nextJob?.Name, nextRun));
    }

    public static AutomationExecution DetermineExecution(AutomationObservation value)
    {
        if (value.ReadFailed) return AutomationExecution.Unavailable;
        if (value.WindowStopping || value.Service == AutomationService.Stopping) return AutomationExecution.Stopping;
        if (value.WindowScheduler) return AutomationExecution.Window;
        if (value.Starting || value.Service == AutomationService.Starting) return AutomationExecution.Starting;
        if (value.ManualRun) return AutomationExecution.OneRun;
        if (value.EngineBusy)
        {
            var age = value.Agent is { } agent ? value.Now - agent.UpdatedUtc : TimeSpan.MaxValue;
            if (value.Agent is { } state && age >= TimeSpan.FromSeconds(-2) && age < TimeSpan.FromSeconds(10))
            {
                if (state.Phase == LocalAgentPhase.Stopping) return AutomationExecution.Stopping;
                if (state.Phase == LocalAgentPhase.Running)
                {
                    // A one-shot --run-job process holds the same engine lock but has no scheduler.
                    if (state.RunsSchedules == false) return AutomationExecution.OneRun;
                    // Older hosts advertised the scheduler through manual-queue support.
                    if (state.RunsSchedules is null && !state.CanRunManually) return AutomationExecution.Busy;
                    return value.Service == AutomationService.Running ? AutomationExecution.Service : AutomationExecution.Background;
                }
            }
            return AutomationExecution.Busy;
        }
        if (value.Service == AutomationService.Running) return AutomationExecution.ServiceNotReady;
        return value.Agent?.Phase == LocalAgentPhase.Faulted ? AutomationExecution.Faulted : AutomationExecution.Off;
    }
}
