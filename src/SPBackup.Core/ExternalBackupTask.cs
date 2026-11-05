namespace SPBackup.Core;

/// <summary>A read-only task: observes external backups without running the backup engine.</summary>
public sealed record ExternalBackupTask(ExternalBackupMonitor Monitor, ExternalBackupReport? Latest,
    ExternalBackupPollResult? Poll)
{
    public Guid Id => Monitor.Id;
    public string Name => Monitor.Name;
    public bool Enabled => Monitor.Enabled;
    public string Kind => Monitor.Kind == ExternalBackupSourceKind.SqlServerHistory ? "Наблюдение MS SQL" : "Наблюдение PostgreSQL";
    public string Source => Monitor.DatabaseName + " · " + (Monitor.Kind == ExternalBackupSourceKind.SqlServerHistory
        ? Monitor.SqlServer : Monitor.ReportDirectory);
    public string ScheduleDescription => "Опрос каждые " + Monitor.PollIntervalSeconds + " с";
    public string ScheduleKind => "Polling";
    public string ResultDescription => Poll?.Error is not null ? "Ошибка опроса"
        : Latest is null ? "Копии пока не обнаружены"
        : Latest.Observation.Outcome == ExternalBackupOutcome.Completed ? "Копия обнаружена" : "Ошибка внешней копии";
    public string ResultDetails => ResultDescription
        + (Poll is null ? "\nОпрос ещё не выполнялся." : "\nПоследний опрос: " + Poll.PolledUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"))
        + (Poll?.Error is { } error ? "\n" + error : "")
        + (Latest is null ? "" : "\n" + Latest.Observation.DatabaseName + " · " + Latest.Observation.FinishedAt.ToString("dd.MM.yyyy HH:mm:ss")
            + " · " + Latest.Observation.TimeZoneId + "\n" + Latest.Observation.EvidenceDescription)
        + "\nНаблюдение не создаёт копии. Результат чтения журнала не подтверждает возможность восстановления.";

    public static IReadOnlyList<ExternalBackupTask> Create(IEnumerable<ExternalBackupMonitor> monitors,
        IEnumerable<ExternalBackupReport> reports, IEnumerable<ExternalBackupPollResult> polls, Guid agentId)
    {
        var ownReports = reports.Where(report => report.AgentId == agentId).ToLookup(report => report.MonitorId);
        var statuses = polls.ToDictionary(poll => poll.MonitorId);
        return monitors.Select(monitor => new ExternalBackupTask(monitor, ownReports[monitor.Id]
                .Where(report => monitor.DatabaseName.Length == 0 || string.Equals(report.Observation.DatabaseName, monitor.DatabaseName,
                    monitor.Kind == ExternalBackupSourceKind.SqlServerHistory ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                .OrderByDescending(report => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(report.Observation.FinishedAt, DateTimeKind.Unspecified),
                    TimeZoneInfo.FindSystemTimeZoneById(report.Observation.TimeZoneId)))
                .ThenByDescending(report => report.ObservedUtc).FirstOrDefault(),
            statuses.GetValueOrDefault(monitor.Id))).ToArray();
    }
}
