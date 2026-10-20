using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace OneCBackup.Core;

/// <summary>Read-only msdb history. Does not run BACKUP, inspect files, or prune external copies.</summary>
public sealed class SqlServerBackupHistorySource(Func<ExternalBackupMonitor, DbConnection>? connectionFactory = null) : IExternalBackupSource
{
    internal const string Query = """
        SELECT CAST(SERVERPROPERTY('ServerName') AS nvarchar(256)),
               bs.backup_set_uuid, bs.database_name, bs.type,
               bs.backup_start_date, bs.backup_finish_date,
               CONVERT(decimal(20,0), bs.backup_size), bmf.physical_device_name
        FROM msdb.dbo.backupset AS bs
        LEFT JOIN msdb.dbo.backupmediafamily AS bmf ON bmf.media_set_id = bs.media_set_id
        WHERE bs.backup_finish_date IS NOT NULL AND bs.backup_set_uuid IS NOT NULL
          AND bs.backup_finish_date >= DATEADD(day, -@days, GETDATE())
          AND (@database = N'' OR bs.database_name = @database)
        ORDER BY bs.backup_set_id, bmf.family_sequence_number, bmf.physical_device_name
        """;

    public async Task<IReadOnlyList<ExternalBackupObservation>> ReadAsync(ExternalBackupMonitor monitor,
        CancellationToken cancellationToken = default)
    {
        monitor.Validate();
        if (monitor.Kind != ExternalBackupSourceKind.SqlServerHistory) throw new ArgumentException("Нужен источник MS SQL.");
        await using var connection = connectionFactory?.Invoke(monitor) ?? new SqlConnection(new SqlConnectionStringBuilder
        {
            DataSource = monitor.SqlServer, InitialCatalog = "msdb", IntegratedSecurity = true,
            Encrypt = SqlConnectionEncryptOption.Mandatory, TrustServerCertificate = false, ConnectTimeout = 15,
            ApplicationName = "1C-Backup monitor"
        }.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = Query;
        command.CommandTimeout = 30;
        AddParameter(command, "@days", System.Data.DbType.Int32, monitor.LookbackDays);
        AddParameter(command, "@database", System.Data.DbType.String, monitor.DatabaseName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var observations = new Dictionary<string, ExternalBackupObservation>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var instance = reader.GetString(0);
            var key = instance + ":" + reader.GetGuid(1).ToString("D");
            if (!observations.TryGetValue(key, out var observation))
            {
                if (observations.Count >= 10000) throw new InvalidDataException("Слишком много копий: уменьшите период или выберите базу.");
                observation = new ExternalBackupObservation(key, instance, reader.GetString(2), reader.GetString(3).Trim(),
                    DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Unspecified),
                    DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Unspecified), monitor.TimeZoneId,
                    ExternalBackupOutcome.Completed, reader.IsDBNull(6) ? null : reader.GetDecimal(6), [], "SqlServerMsdbHistory");
            }
            if (!reader.IsDBNull(7)) observation = observation with
                { Locations = observation.Locations.Append(reader.GetString(7)).Distinct().ToArray() };
            observations[key] = observation;
        }
        return observations.Values.ToArray();
    }

    private static void AddParameter(DbCommand command, string name, System.Data.DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name; parameter.DbType = type; parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
