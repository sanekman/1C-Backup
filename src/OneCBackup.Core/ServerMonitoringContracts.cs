using System.Text.Json;
#if SERVER_CONTRACTS
namespace OneCBackup.Server.Monitoring;
#else
namespace OneCBackup.Core.Monitoring;
#endif

// Deliberately excludes source paths, connection strings, credentials and exception text.
public sealed record MonitoredJob(Guid JobId, string Name, string SourceKind, bool Enabled, string Schedule,
    DateTimeOffset? NextRunUtc, int DestinationCount);
public sealed record MonitoredDestination(int Index, bool Succeeded, long Bytes, bool Incomplete);
public sealed record MonitoredRun(Guid RunId, Guid JobId, string JobName, DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc, string Status, int WarningCount, bool Incomplete, string? CopyKind,
    IReadOnlyList<MonitoredDestination> Destinations);
public sealed record MonitoredProgress(Guid RunId, Guid JobId, string Stage, int? Percent, DateTimeOffset UpdatedUtc);
public sealed record MonitoredSource(Guid MonitorId, string Name, string Kind, bool Enabled, DateTimeOffset? PolledUtc, bool Failed);
public sealed record MonitoringSnapshot(DateTimeOffset SentUtc, string AgentVersion, IReadOnlyList<MonitoredJob> Jobs,
    MonitoredProgress? Progress, int PendingRuns, IReadOnlyList<MonitoredSource>? Sources = null, int CollectionWarnings = 0);
public sealed record MonitoringState(DateTimeOffset ReceivedUtc, MonitoringSnapshot Snapshot);
public sealed record MonitoredRunPage(IReadOnlyList<MonitoredRun> Runs, int Page, int PageSize);
public static class MonitoringValidation
{
    private static bool Text(string? text, int limit) => !string.IsNullOrWhiteSpace(text) && text.Length <= limit && !text.Any(char.IsControl);
    public static bool Valid(MonitoringSnapshot? s) => s is not null && s.SentUtc != default && s.SentUtc.Offset == TimeSpan.Zero
        && Text(s.AgentVersion,128) && s.PendingRuns >= 0 && s.CollectionWarnings >= 0 && s.Jobs is { Count: <= 1000 }
        && s.Jobs.All(j => j is not null && j.JobId != Guid.Empty && Text(j.Name,256) && Text(j.SourceKind,64)
            && Text(j.Schedule,512) && j.DestinationCount is >= 1 and <= 100)
        && s.Jobs.Select(j=>j.JobId).Distinct().Count() == s.Jobs.Count
        && (s.Sources is null || s.Sources.Count <= 1000 && s.Sources.All(m=>m is not null && m.MonitorId != Guid.Empty && Text(m.Name,256) && Text(m.Kind,64))
            && s.Sources.Select(m=>m.MonitorId).Distinct().Count()==s.Sources.Count)
        && (s.Progress is null || s.Progress.RunId != Guid.Empty && s.Progress.JobId != Guid.Empty
            && Text(s.Progress.Stage,64) && s.Progress.Percent is null or >= 0 and <= 100 && s.Progress.UpdatedUtc != default);
    public static bool Valid(MonitoredRun? r) => r is not null && r.RunId != Guid.Empty && r.JobId != Guid.Empty
        && Text(r.JobName,256) && r.StartedUtc != default && r.FinishedUtc >= r.StartedUtc
        && r.StartedUtc.Offset == TimeSpan.Zero && r.FinishedUtc.Offset == TimeSpan.Zero
        && r.Status is "Succeeded" or "Failed" or "Cancelled" or "Interrupted" or "PartiallySucceeded"
        && r.WarningCount >= 0 && (r.CopyKind is null or "Full" or "Incremental")
        && r.Destinations is { Count: <= 100 } && r.Destinations.All(d=> d is not null && d.Index >= 0 && d.Bytes >= 0)
        && r.Destinations.Select(d=>d.Index).Distinct().Count() == r.Destinations.Count;
}
