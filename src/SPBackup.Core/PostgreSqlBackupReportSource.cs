using System.Text.Json;
using System.Text.Json.Serialization;

namespace SPBackup.Core;

/// <summary>Imports immutable reports published by the external PostgreSQL backup script.</summary>
public sealed class PostgreSqlBackupReportSource : IExternalBackupSource
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public async Task<IReadOnlyList<ExternalBackupObservation>> ReadAsync(ExternalBackupMonitor monitor,
        CancellationToken cancellationToken = default)
    {
        monitor.Validate();
        if (monitor.Kind != ExternalBackupSourceKind.PostgreSqlReports) throw new ArgumentException("Нужен источник отчётов PostgreSQL.");
        if (!Directory.Exists(monitor.ReportDirectory)) throw new DirectoryNotFoundException("Каталог отчётов PostgreSQL недоступен.");
        var result = new List<ExternalBackupObservation>();
        foreach (var path in Directory.EnumerateFiles(monitor.ReportDirectory, "*.json").Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Count >= 10000) throw new InvalidDataException("Слишком много отчётов PostgreSQL.");
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 1024 * 1024) throw new InvalidDataException("Слишком большой отчёт PostgreSQL: " + path);
            var observation = await JsonSerializer.DeserializeAsync<ExternalBackupObservation>(input, Options, cancellationToken)
                ?? throw new InvalidDataException("Пустой отчёт PostgreSQL: " + path);
            observation.Validate();
            if (monitor.DatabaseName.Length == 0 || observation.DatabaseName == monitor.DatabaseName) result.Add(observation);
        }
        return result;
    }
}
