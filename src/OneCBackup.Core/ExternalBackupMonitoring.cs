using System.Text.Json;

namespace OneCBackup.Core;

public enum ExternalBackupSourceKind { SqlServerHistory, PostgreSqlReports }
public enum ExternalBackupOutcome { Completed, Failed }

public sealed record ExternalBackupMonitor
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public ExternalBackupSourceKind Kind { get; init; }
    public string SqlServer { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public SqlServerAuthentication SqlAuthentication { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? SqlCredentialKey { get; init; }
    public string DatabaseName { get; init; } = "";
    public string TimeZoneId { get; init; } = "UTC";
    public int LookbackDays { get; init; } = 30;
    public string ReportDirectory { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public int PollIntervalSeconds { get; init; } = 60;

    public void Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || !Enum.IsDefined(Kind) || LookbackDays is < 1 or > 3650)
            throw new InvalidDataException("Укажите имя источника и период истории от 1 до 3650 дней.");
        if (PollIntervalSeconds is < 15 or > 86400)
            throw new InvalidDataException("Интервал опроса источника должен быть от 15 до 86400 секунд.");
        if (Kind == ExternalBackupSourceKind.SqlServerHistory && string.IsNullOrWhiteSpace(SqlServer))
            throw new InvalidDataException("Укажите экземпляр MS SQL.");
        if (Kind == ExternalBackupSourceKind.SqlServerHistory)
        {
            var errors = SqlServerConnectionFactory.ValidateAuthentication(SqlAuthentication, SqlCredentialKey);
            if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
        }
        if (Kind == ExternalBackupSourceKind.PostgreSqlReports && !Path.IsPathFullyQualified(ReportDirectory))
            throw new InvalidDataException("Укажите полный путь к каталогу отчётов PostgreSQL.");
        try { TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId); }
        catch (Exception ex) when (ex is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new InvalidDataException("Укажите часовой пояс MS SQL.", ex); }
    }
}

// SQL history timestamps have no offset: retain their server time zone rather than invent UTC.
public sealed record ExternalBackupObservation(string SourceKey, string Instance, string DatabaseName,
    string BackupType, DateTime StartedAt, DateTime FinishedAt, string TimeZoneId,
    ExternalBackupOutcome Outcome, decimal? SizeBytes, IReadOnlyList<string> Locations,
    string Evidence, string? Error = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string OutcomeDescription => Outcome == ExternalBackupOutcome.Completed ? "Завершено" : "Ошибка";
    [System.Text.Json.Serialization.JsonIgnore]
    public string LocationDescription => Locations is null ? "" : string.Join("; ", Locations);
    [System.Text.Json.Serialization.JsonIgnore]
    public string EvidenceDescription => Evidence switch
    {
        "SqlServerMsdbHistory" => "История MS SQL",
        "PgRestoreCatalogChecked" => "Каталог дампа проверен",
        "PostgreSqlScriptFailure" => "Ошибка скрипта PostgreSQL",
        _ => Evidence
    };

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SourceKey) || string.IsNullOrWhiteSpace(Instance) ||
            string.IsNullOrWhiteSpace(DatabaseName) || string.IsNullOrWhiteSpace(BackupType) ||
            string.IsNullOrWhiteSpace(Evidence) || !Enum.IsDefined(Outcome) ||
            StartedAt == default || FinishedAt == default || SizeBytes < 0 ||
            Locations is null || Locations.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Некорректный отчёт о внешней копии.");
        TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
    }
}

public interface IExternalBackupSource
{
    Task<IReadOnlyList<ExternalBackupObservation>> ReadAsync(ExternalBackupMonitor monitor,
        CancellationToken cancellationToken = default);
}

public sealed class ExternalBackupMonitorStore(string path)
{
    private readonly AgentJsonFile<List<ExternalBackupMonitor>> file = new(path);
    public async Task<IReadOnlyList<ExternalBackupMonitor>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var monitors = await file.ReadAsync(cancellationToken) ?? [];
        Validate(monitors);
        return monitors;
    }
    public Task SaveAsync(IReadOnlyList<ExternalBackupMonitor> monitors, CancellationToken cancellationToken = default)
    {
        Validate(monitors);
        return file.WriteAsync(monitors.ToList(), cancellationToken);
    }
    private static void Validate(IReadOnlyList<ExternalBackupMonitor> monitors)
    {
        foreach (var monitor in monitors) (monitor ?? throw new InvalidDataException("Пустой источник.")).Validate();
        if (monitors.Select(m => m.Id).Distinct().Count() != monitors.Count)
            throw new InvalidDataException("Повторяются идентификаторы источников.");
    }
}

public sealed record ExternalBackupReport(Guid ReportId, Guid AgentId, Guid MonitorId,
    string? TargetServer, DateTimeOffset ObservedUtc, ExternalBackupObservation Observation,
    DateTimeOffset? DeliveredUtc = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string DeliveryDescription => DeliveredUtc is not null ? "Отправлено" :
        TargetServer is null ? "Локальная история" : "Ожидает отправки";
}

public interface IExternalBackupReportApi
{
    Task SendExternalBackupAsync(AgentSettings settings, ExternalBackupReport report,
        CancellationToken cancellationToken = default);
}

/// <summary>Persistent observations and outbox. One instance per host data directory.</summary>
public sealed class ExternalBackupOutbox(string path)
{
    private readonly AgentJsonFile<List<ExternalBackupReport>> file = new(path);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim sending = new(1, 1);

    public async Task<IReadOnlyList<ExternalBackupReport>> ReadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { return await ReadCoreAsync(cancellationToken); }
        finally { gate.Release(); }
    }

    public async Task<int> ObserveAsync(AgentSettings settings, ExternalBackupMonitor monitor,
        IReadOnlyList<ExternalBackupObservation> observations, CancellationToken cancellationToken = default)
    {
        monitor.Validate();
        if (settings.Validate().Count != 0) throw new InvalidDataException("Некорректные настройки агента.");
        foreach (var observation in observations) observation.Validate();
        await gate.WaitAsync(cancellationToken);
        try
        {
            var reports = await ReadCoreAsync(cancellationToken);
            var count = 0;
            foreach (var observation in observations)
            {
                var existing = reports.SingleOrDefault(r => r.AgentId == settings.AgentId && r.MonitorId == monitor.Id &&
                    r.Observation.SourceKey == observation.SourceKey);
                if (existing is not null)
                {
                    if (JsonSerializer.Serialize(existing.Observation) != JsonSerializer.Serialize(observation))
                        throw new InvalidDataException("Содержимое ранее полученного отчёта изменилось: " + observation.SourceKey);
                    continue;
                }
                reports.Add(new ExternalBackupReport(Guid.NewGuid(), settings.AgentId, monitor.Id,
                    settings.Mode == AgentMode.Managed ? settings.NormalizedServerAddress : null,
                    DateTimeOffset.UtcNow, observation));
                count++;
            }
            if (count != 0) await file.WriteAsync(reports, cancellationToken);
            return count;
        }
        finally { gate.Release(); }
    }

    public async Task<int> SendPendingAsync(AgentSettings settings, IExternalBackupReportApi api,
        CancellationToken cancellationToken = default)
    {
        if (settings.Mode == AgentMode.Standalone) return 0;
        if (settings.Validate().Count != 0) throw new InvalidDataException("Некорректные настройки агента.");
        await sending.WaitAsync(cancellationToken);
        try
        {
            var pending = (await ReadAsync(cancellationToken)).Where(r => r.DeliveredUtc is null &&
                r.AgentId == settings.AgentId && r.TargetServer == settings.NormalizedServerAddress).ToArray();
            var count = 0;
            foreach (var report in pending)
            {
                await api.SendExternalBackupAsync(settings, report, cancellationToken);
                await gate.WaitAsync(CancellationToken.None);
                try
                {
                    var reports = await ReadCoreAsync(CancellationToken.None);
                    var index = reports.FindIndex(r => r.ReportId == report.ReportId);
                    if (index < 0) throw new InvalidDataException("Отчёт исчез из очереди.");
                    reports[index] = reports[index] with { DeliveredUtc = DateTimeOffset.UtcNow };
                    await file.WriteAsync(reports, CancellationToken.None);
                }
                finally { gate.Release(); }
                count++;
            }
            return count;
        }
        finally { sending.Release(); }
    }

    private async Task<List<ExternalBackupReport>> ReadCoreAsync(CancellationToken cancellationToken)
    {
        var reports = await file.ReadAsync(cancellationToken) ?? [];
        if (reports.Any(r => r is null || r.ReportId == Guid.Empty || r.AgentId == Guid.Empty || r.MonitorId == Guid.Empty ||
            r.Observation is null) || reports.Select(r => r.ReportId).Distinct().Count() != reports.Count ||
            reports.Select(r => (r.AgentId, r.MonitorId, r.Observation.SourceKey)).Distinct().Count() != reports.Count)
            throw new InvalidDataException("Повреждена история внешних копий.");
        foreach (var report in reports) report.Observation.Validate();
        return reports;
    }
}
