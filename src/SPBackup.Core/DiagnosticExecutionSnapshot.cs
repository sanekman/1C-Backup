using System.Text.Json;

namespace SPBackup.Core;

/// <summary>The saved plan displayed by the window, not the dispatcher's in-memory queue.</summary>
public sealed record DiagnosticWindowSnapshot(DateTimeOffset CapturedUtc, AgentMode? Mode,
    long? ConfigurationRevision, bool ConfigurationReady, bool WindowSchedulerRunning,
    Guid? ActiveJobId, IReadOnlyList<DiagnosticJobSchedule> Jobs)
{
    public static DiagnosticWindowSnapshot Capture(IReadOnlyList<BackupJob> jobs, AgentMode? mode,
        long? revision, bool configurationReady, bool schedulerRunning, Guid? activeJobId)
    {
        var now = DateTimeOffset.UtcNow;
        return new(now, mode, revision, configurationReady, schedulerRunning, activeJobId,
            jobs.Take(10000).Select(job => DiagnosticJobSchedule.Capture(job, now)).ToArray());
    }
}

public enum DiagnosticScheduleTrigger { Disabled, Manual, SystemStartup, Scheduled, InvalidSchedule }
public sealed record DiagnosticJobSchedule(Guid JobId, bool Enabled, DiagnosticScheduleTrigger Trigger,
    DateTimeOffset? CalculatedNextRunUtc, bool UsesComputerTimeZone)
{
    internal static DiagnosticJobSchedule Capture(BackupJob job, DateTimeOffset now)
    {
        try
        {
            var next = BackupSchedule.NextRun(job, now);
            var trigger = !job.Enabled ? DiagnosticScheduleTrigger.Disabled : job.ScheduleKind switch
            {
                ScheduleKind.Manual => DiagnosticScheduleTrigger.Manual,
                ScheduleKind.SystemStartup => DiagnosticScheduleTrigger.SystemStartup,
                _ => DiagnosticScheduleTrigger.Scheduled
            };
            return new(job.Id, job.Enabled, trigger, next, job.TimeZoneId == BackupSchedule.LocalTimeZone);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException
            or TimeZoneNotFoundException or InvalidTimeZoneException)
        { return new(job.Id, job.Enabled, DiagnosticScheduleTrigger.InvalidSchedule, null, false); }
    }
}

/// <summary>Only allowlisted scalar fields leave the profile; no raw errors, names or settings.</summary>
internal static class DiagnosticExecutionSnapshot
{
    internal static async Task<(object Snapshot, int Skipped)> ReadAsync(string profile,
        DiagnosticWindowSnapshot? window, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var skipped = 0;
        async Task<(T? Value, string Status)> Read<T>(string path, long limit) where T : class
        {
            try
            {
                var value = await new AgentJsonFile<T>(path).ReadBoundedAsync(limit, token);
                return (value, value is null ? "Missing" : "Available");
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            { skipped++; return (null, "Unavailable"); }
        }

        var (state, agentStatus) = await Read<LocalAgentState>(Path.Combine(profile, "local-agent.json"), 1024 * 1024);
        object? agent = null;
        if (state is not null)
        {
            if (state.SessionId == Guid.Empty || state.ProcessId <= 0 || !Enum.IsDefined(state.Phase)
                || state.ExecutionMode is { } mode && !Enum.IsDefined(mode)
                || state.StartedUtc == default || state.UpdatedUtc < state.StartedUtc || state.ConfigurationRevision is <= 0)
            { skipped++; agentStatus = "Unavailable"; }
            else agent = new { state.SessionId, state.ProcessId, state.StartedUtc, state.UpdatedUtc, state.Phase,
                state.ExecutionMode, state.ConfigurationRevision, state.RunsSchedules, state.CanRunManually,
                state.CanCancelCurrentRun, state.CanCheckStorages,
                HasError = !string.IsNullOrEmpty(state.Error), HasWarning = !string.IsNullOrEmpty(state.Warning),
                HasMonitoringError = !string.IsNullOrEmpty(state.MonitoringError),
                SnapshotAgeMilliseconds = (long)(now - state.UpdatedUtc).TotalMilliseconds };
        }

        var (progress, progressStatus) = await Read<BackupProgressSnapshot>(Path.Combine(profile, "backup-progress.json"), 1024 * 1024);
        object? run = null;
        if (progress is not null)
        {
            if (progress.RunId == Guid.Empty || progress.JobId == Guid.Empty || progress.ProcessId <= 0
                || progress.Progress is null || !Enum.IsDefined(progress.Progress.Stage) || progress.UpdatedUtc == default)
            { skipped++; progressStatus = "Unavailable"; }
            else run = new { progress.RunId, progress.JobId, progress.ProcessId, progress.Sequence,
                progress.UpdatedUtc, progress.Progress.Stage, progress.Progress.Completed, progress.Progress.Total,
                progress.Finished, SnapshotAgeMilliseconds = (long)(now - progress.UpdatedUtc).TotalMilliseconds };
        }

        var pending = new List<LocalManualRunRequest>();
        var pendingStatus = "Missing";
        var truncated = false;
        var directory = Path.Combine(profile, "manual-runs", "pending");
        try
        {
            if (Directory.Exists(directory))
            {
                FileBackupPaths.RejectReparse(Path.Combine(profile, "manual-runs"));
                FileBackupPaths.RejectReparse(directory);
                pendingStatus = "Available";
                var paths = Directory.EnumerateFiles(directory, "*.json").Take(1001).ToArray();
                truncated = paths.Length > 1000;
                foreach (var path in paths.Take(1000))
                {
                    token.ThrowIfCancellationRequested();
                    var (request, status) = await Read<LocalManualRunRequest>(path, 8192);
                    if (status == "Unavailable") pendingStatus = "Partial";
                    if (request is null) continue;
                    if (request.RequestId == Guid.Empty || request.SessionId == Guid.Empty || request.JobId == Guid.Empty
                        || request.SubmittedUtc == default || Path.GetFileName(path) != request.RequestId.ToString("N") + ".json")
                    { skipped++; pendingStatus = "Partial"; continue; }
                    pending.Add(request);
                }
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        { skipped++; pendingStatus = "Unavailable"; }

        return (new { FormatVersion = 1, CapturedUtc = now, LocalTime = now.ToLocalTime(),
            Window = window, AgentStatus = agentStatus, Agent = agent,
            ProgressStatus = progressStatus, LastProgress = run,
            PendingManualRequestsStatus = pendingStatus, PendingManualRequestsTruncated = truncated,
            PendingManualRequests = pending.OrderBy(r => r.SubmittedUtc).ThenBy(r => r.RequestId).ToArray() }, skipped);
    }
}
