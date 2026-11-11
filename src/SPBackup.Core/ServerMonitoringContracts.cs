using System.Text.Json;
#if SERVER_CONTRACTS
namespace SPBackup.Server.Monitoring;
#else
namespace SPBackup.Core.Monitoring;
#endif

// Deliberately excludes source paths, connection strings, credentials and exception text.
public sealed record MonitoredIssue(string Code, string Severity, string Stage,
    int? DestinationIndex = null, int? NativeCode = null);
public static class MonitoredIssueCodes
{
    public static bool Known(string? code) => code is "AccessDenied" or "NetworkLogonFailed" or "NetworkCredentialConflict"
        or "NetworkUnavailable" or "DiskFull" or "FileNotFound" or "FileBusy" or "Timeout" or "TlsFailed"
        or "HttpFailed" or "ExternalBackupFailed" or "BackupFailed" or "SourceIncomplete" or "OneCOpenDatabase" or "RetentionChainPreserved"
        or "RetentionIncompletePreserved" or "RetentionSkippedIncomplete" or "RetentionFailed"
        or "SnapshotIndexNotSaved" or "WorkCleanupFailed" or "Cancelled" or "Interrupted" or "AdditionalWarnings";
    public static bool Valid(MonitoredIssue? issue) => issue is not null && Known(issue.Code)
        && issue.Severity is "Warning" or "Error"
        && issue.Stage is "Source" or "Storage" or "Retention" or "WorkDirectory" or "SnapshotIndex" or "Backup"
        && issue.DestinationIndex is null or >= 0 and < 100 && issue.NativeCode is null or >= 0 and <= 65535;
}
public sealed record MonitoredJob(Guid JobId, string Name, string SourceKind, bool Enabled, string Schedule,
    DateTimeOffset? NextRunUtc, int DestinationCount)
{
    public string TaskKind { get; init; } = "Backup";
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? BackupScope { get; init; }
    public int? MaximumBackupAgeHours { get; init; }
    public bool? LocalAccessReady { get; init; }
}
public sealed record MonitoredDestination(int Index, bool Succeeded, long Bytes, bool Incomplete);
public sealed record MonitoredRun(Guid RunId, Guid JobId, string JobName, DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc, string Status, int WarningCount, bool Incomplete, string? CopyKind,
    IReadOnlyList<MonitoredDestination> Destinations, string? Evidence = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public long? ConfigurationRevision { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? BackupScope { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<MonitoredIssue>? Issues { get; init; }
}
public sealed record MonitoredProgress(Guid RunId, Guid JobId, string Stage, int? Percent, DateTimeOffset UpdatedUtc);
public sealed record MonitoredSource(Guid MonitorId, string Name, string Kind, bool Enabled, DateTimeOffset? PolledUtc, bool Failed)
{
    public int? MaximumBackupAgeHours { get; init; }
    public DateTimeOffset? LastSuccessfulUtc { get; init; }
    public bool FreshnessWarning { get; init; }
    public string? ObservationStatus { get; init; }
    public Guid? LatestReportId { get; init; }
    public DateTimeOffset? LatestFinishedUtc { get; init; }
    public string? PollErrorCode { get; init; }
    public bool SqlBackupTypesReported { get; init; }
    public DateTimeOffset? LastFullUtc { get; init; }
    public DateTimeOffset? LastDifferentialUtc { get; init; }
    public DateTimeOffset? LastLogUtc { get; init; }
}
public static class MonitoredPollErrors
{
    public static bool Known(string code) => code is "Certificate" or "Login" or "Access" or "Timeout" or "Connection" or "Authentication" or "Read";
    public static string Describe(string? code) => code switch
    {
        "Certificate" => "Не подтверждён сертификат MS SQL. Проверьте имя сервера, срок действия и доверие к сертификату на компьютере агента.",
        "Login" => "MS SQL отклонил вход. Проверьте способ входа и сохранённый пароль. При Windows-входе проверьте учётную запись службы агента.",
        "Access" => "Недостаточно доступа к базе или журналу копий. Для MS SQL проверьте доступ к msdb и права чтения истории; для файловых отчётов — права на папку.",
        "Timeout" => "Истекло время ожидания ответа. Проверьте доступность и нагрузку сервера.",
        "Connection" => "Не удалось установить или сохранить соединение. Проверьте адрес, экземпляр, порт, сеть и брандмауэр.",
        "Authentication" => "Не подтверждена подлинность подключения. Проверьте настройки входа и TLS; подробности доступны в агенте.",
        _ => "Не удалось прочитать журнал внешних копий. Проверьте настройки задания и подробности ошибки в агенте."
    };
}

public sealed record MonitoringSnapshot(DateTimeOffset SentUtc, string AgentVersion, IReadOnlyList<MonitoredJob> Jobs,
    MonitoredProgress? Progress, int PendingRuns, IReadOnlyList<MonitoredSource>? Sources = null, int CollectionWarnings = 0)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutionMode { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? BackupExecutionAvailable { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public long? ConfigurationRevision { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ConfigurationCapabilities { get; init; }
}
public sealed record MonitoringState(DateTimeOffset ReceivedUtc, MonitoringSnapshot Snapshot);
public sealed record MonitoredRunPage(IReadOnlyList<MonitoredRun> Runs, int Page, int PageSize);
public static class MonitoringValidation
{
    private static bool Text(string? text, int limit) => !string.IsNullOrWhiteSpace(text) && text.Length <= limit && !text.Any(char.IsControl);
    public static bool Valid(MonitoringSnapshot? s) => s is not null && s.SentUtc != default && s.SentUtc.Offset == TimeSpan.Zero
        && s.ExecutionMode is null or "Standalone" or "Managed" && s.ConfigurationRevision is null or > 0
        && (s.ExecutionMode != "Standalone" || s.ConfigurationRevision is null)
        && Text(s.AgentVersion,128) && s.PendingRuns >= 0 && s.CollectionWarnings >= 0 && s.Jobs is { Count: <= 1000 }
        && (s.ConfigurationCapabilities is null || AgentCapabilities.Valid(s.ConfigurationCapabilities))
        && s.Jobs.All(j => j is not null && (j.BackupScope is null || j.BackupScope.Length == 64 && j.BackupScope.All(Uri.IsHexDigit)) && j.MaximumBackupAgeHours is not (< 1 or > 87600) && j.JobId != Guid.Empty && Text(j.Name,256) && Text(j.SourceKind,64)
            && Text(j.Schedule,512) && (j.TaskKind == "Backup" && j.DestinationCount is >= 1 and <= 100
                || j.TaskKind == "Observation" && j.DestinationCount == 0 && j.SourceKind is "SqlServerHistory" or "PostgreSqlReports"))
        && s.Jobs.Select(j=>j.JobId).Distinct().Count() == s.Jobs.Count
        && (s.Sources is null || s.Sources.Count <= 1000 && s.Sources.All(m=>m is not null && m.MonitorId != Guid.Empty && Text(m.Name,256) && Text(m.Kind,64)
                && m.MaximumBackupAgeHours is null or >= 1 and <= 87600
                && m.ObservationStatus is null or "PollFailed" or "BackupFailed" or "Overdue" or "NoCopies" or "CopyFound"
                && (m.PollErrorCode is null || m.Failed && MonitoredPollErrors.Known(m.PollErrorCode))
                && (!m.SqlBackupTypesReported || m.Kind == "SqlServerHistory")
                && (m.LastFullUtc is null || m.LastFullUtc.Value.Offset == TimeSpan.Zero)
                && (m.LastDifferentialUtc is null || m.LastDifferentialUtc.Value.Offset == TimeSpan.Zero)
                && (m.LastLogUtc is null || m.LastLogUtc.Value.Offset == TimeSpan.Zero)
                && (m.SqlBackupTypesReported || m.LastFullUtc is null && m.LastDifferentialUtc is null && m.LastLogUtc is null)
                && (m.LatestReportId is null || m.LatestReportId != Guid.Empty)
                && (m.LatestFinishedUtc is null || m.LatestFinishedUtc.Value.Offset == TimeSpan.Zero)
                && (m.LastSuccessfulUtc is null || m.LastSuccessfulUtc.Value.Offset == TimeSpan.Zero)
                && (!m.FreshnessWarning || m.Enabled && !m.Failed && m.PolledUtc is not null && m.MaximumBackupAgeHours is not null))
            && s.Sources.Select(m=>m.MonitorId).Distinct().Count()==s.Sources.Count)
        && (s.Progress is null || s.Progress.RunId != Guid.Empty && s.Progress.JobId != Guid.Empty
            && Text(s.Progress.Stage,64) && s.Progress.Percent is null or >= 0 and <= 100 && s.Progress.UpdatedUtc != default);
    public static bool Valid(MonitoredRun? r) => r is not null && (r.BackupScope is null || r.BackupScope.Length == 64 && r.BackupScope.All(Uri.IsHexDigit)) && r.RunId != Guid.Empty && r.JobId != Guid.Empty
        && r.ConfigurationRevision is null or > 0
        && Text(r.JobName,256) && r.StartedUtc != default && r.FinishedUtc >= r.StartedUtc
        && r.StartedUtc.Offset == TimeSpan.Zero && r.FinishedUtc.Offset == TimeSpan.Zero
        && r.Status is "Succeeded" or "Failed" or "Cancelled" or "Interrupted" or "PartiallySucceeded"
        && r.Evidence is null or "SqlServerMsdbHistory" or "PgRestoreCatalogChecked" or "PostgreSqlScriptFailure" or "ExternalReport"
        && r.WarningCount >= 0 && (r.CopyKind is null or "Full" or "Incremental")
        && (r.Issues is null || r.Issues.Count <= 256 && r.Issues.All(MonitoredIssueCodes.Valid))
        && r.Destinations is { Count: <= 100 } && r.Destinations.All(d=> d is not null && d.Index >= 0 && d.Bytes >= 0)
        && r.Destinations.Select(d=>d.Index).Distinct().Count() == r.Destinations.Count;
}
