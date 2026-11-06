namespace SPBackup.Core;

/// <summary>A read-only task: observes external backups without running the backup engine.</summary>
public sealed record ExternalBackupTask(ExternalBackupMonitor Monitor, ExternalBackupReport? Latest,
    ExternalBackupPollResult? Poll)
{
    public DateTimeOffset EvaluatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSuccessfulUtc { get; init; }
    public DateTimeOffset? LastFullUtc { get; init; }
    public DateTimeOffset? LastDifferentialUtc { get; init; }
    public DateTimeOffset? LastLogUtc { get; init; }
    private static string CopyTime(DateTimeOffset? value) => value?.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") ?? "не обнаружена";
    public string BackupTypesDetails => Monitor.Kind != ExternalBackupSourceKind.SqlServerHistory ? ""
        : "\nПоследние успешные копии из доступной истории:"
            + "\nПолная: " + CopyTime(LastFullUtc)
            + "\nДифференциальная: " + CopyTime(LastDifferentialUtc)
            + "\nЖурнал транзакций: " + CopyTime(LastLogUtc);
    public bool FreshnessWarning => Monitor.Enabled && Monitor.MaximumBackupAgeHours is { } hours
        && Poll is { Error: null } && (LastSuccessfulUtc is null || EvaluatedUtc - LastSuccessfulUtc > TimeSpan.FromHours(hours));
    public string FreshnessDetails => Monitor.MaximumBackupAgeHours is { } hours
        ? $"Допустимый возраст успешной копии: {hours} ч. " + (LastSuccessfulUtc is { } last
            ? "Последняя успешная копия: " + last.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss")
            : "Успешная копия выбранной базы не обнаружена.") : "";
    public Guid Id => Monitor.Id;
    public string Name => Monitor.Name;
    public bool Enabled => Monitor.Enabled;
    public string Kind => Monitor.Kind == ExternalBackupSourceKind.SqlServerHistory ? "Наблюдение MS SQL" : "Наблюдение PostgreSQL";
    public string Source => Monitor.DatabaseName + " · " + (Monitor.Kind == ExternalBackupSourceKind.SqlServerHistory
        ? Monitor.SqlServer : Monitor.ReportDirectory);
    public string ScheduleDescription => "Опрос каждые " + Monitor.PollIntervalSeconds + " с";
    public string ScheduleKind => "Polling";
    public string ResultCode => Poll?.Error is not null ? "PollFailed"
        : Latest?.Observation.Outcome == ExternalBackupOutcome.Failed ? "BackupFailed"
        : FreshnessWarning ? "Overdue"
        : Latest is null ? "NoCopies" : "CopyFound";
    public string ResultDescription => ResultCode switch
    {
        "PollFailed" => "Ошибка опроса",
        "BackupFailed" => "Ошибка внешней копии",
        "Overdue" => "Нет свежей копии",
        "NoCopies" => "Копии пока не обнаружены",
        _ => "Копия обнаружена"
    };
    public DateTimeOffset? LatestFinishedUtc => Latest is null ? null : FinishedUtc(Latest);
    public string ResultDetails => ResultDescription + (FreshnessDetails.Length == 0 ? "" : "\n" + FreshnessDetails)
        + (Poll is null ? "\nОпрос ещё не выполнялся." : "\nПоследний опрос: " + Poll.PolledUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"))
        + (Poll?.Error is { } error ? "\n" + Monitoring.MonitoredPollErrors.Describe(Poll.ErrorCode) + "\n" + error : "")
        + (Latest is null ? "" : "\n" + Latest.Observation.DatabaseName + " · " + Latest.Observation.FinishedAt.ToString("dd.MM.yyyy HH:mm:ss")
            + " · " + Latest.Observation.TimeZoneId + "\n" + Latest.Observation.EvidenceDescription)
        + BackupTypesDetails
        + "\nНаблюдение не создаёт копии. Результат чтения журнала не подтверждает возможность восстановления.";

    public static IReadOnlyList<ExternalBackupTask> Create(IEnumerable<ExternalBackupMonitor> monitors,
        IEnumerable<ExternalBackupReport> reports, IEnumerable<ExternalBackupPollResult> polls, Guid agentId, DateTimeOffset? now = null)
    {
        var ownReports = reports.Where(report => report.AgentId == agentId).ToLookup(report => report.MonitorId);
        var statuses = polls.ToDictionary(poll => poll.MonitorId);
        return monitors.Select(monitor =>
        {
            var matching = ownReports[monitor.Id].Where(report => monitor.DatabaseName.Length == 0 ||
                string.Equals(report.Observation.DatabaseName, monitor.DatabaseName,
                    monitor.Kind == ExternalBackupSourceKind.SqlServerHistory ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                .OrderByDescending(report => FinishedUtc(report)).ThenByDescending(report => report.ObservedUtc).ToArray();
            var successful = matching.FirstOrDefault(report => report.Observation.Outcome == ExternalBackupOutcome.Completed);
            DateTimeOffset? LatestType(string type)
            {
                if (monitor.Kind != ExternalBackupSourceKind.SqlServerHistory) return null;
                var report = matching.FirstOrDefault(value => value.Observation.Outcome == ExternalBackupOutcome.Completed
                    && value.Observation.BackupType == type);
                return report is null ? null : FinishedUtc(report);
            }
            return new ExternalBackupTask(monitor, matching.FirstOrDefault(), statuses.GetValueOrDefault(monitor.Id))
            { EvaluatedUtc = now ?? DateTimeOffset.UtcNow, LastSuccessfulUtc = successful is null ? null : FinishedUtc(successful),
                LastFullUtc = LatestType("D"), LastDifferentialUtc = LatestType("I"), LastLogUtc = LatestType("L") };
        }).ToArray();
    }

    private static DateTimeOffset FinishedUtc(ExternalBackupReport report) => new(TimeZoneInfo.ConvertTimeToUtc(
        DateTime.SpecifyKind(report.Observation.FinishedAt, DateTimeKind.Unspecified),
        TimeZoneInfo.FindSystemTimeZoneById(report.Observation.TimeZoneId)));
}
