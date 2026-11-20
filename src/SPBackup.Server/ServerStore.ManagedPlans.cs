namespace SPBackup.Server;

public sealed partial class ServerStore
{
    internal async Task<Dictionary<Guid, ManagedPlanStatus>> ManagedPlansAsync(Guid? agent, DateTimeOffset cutoff, CancellationToken token)
    {
        await using var db = await source.OpenConnectionAsync(token);
        await using var command = Command(db, null, $"""
            SELECT a.id,c.revision,offered.status,offered.reason,accepted.revision,accepted.received_utc,
                   a.reported_revision,a.received_utc,a.execution_available,
                   (m.document->>'configurationRevision')::bigint,m.received_utc,access.checked,access.missing,
                   (m.document->>'backupExecutionAvailable')::boolean
            FROM server_agents a JOIN server_configurations c ON c.agent_id=a.id
            {ConfigurationOffersJoin}
            LEFT JOIN server_monitoring m ON m.agent_id=a.id AND m.document->>'executionMode'='Managed'
            LEFT JOIN LATERAL (
                SELECT count(*) FILTER (WHERE job->>'localAccessReady' IS NOT NULL)::int checked,
                       count(*) FILTER (WHERE job->>'localAccessReady'='false')::int missing
                FROM jsonb_array_elements(m.document->'jobs') AS job
                WHERE (job->>'enabled')::boolean AND coalesce(job->>'taskKind','Backup')='Backup'
            ) access ON true
            WHERE NOT a.revoked AND (@id::uuid IS NULL OR a.id=@id)
                AND (c.revision>1 OR offered.offer_id IS NOT NULL OR accepted.revision IS NOT NULL
                     OR a.received_utc IS NOT NULL OR m.received_utc IS NOT NULL)
            """, ("id", agent));
        await using var reader = await command.ExecuteReaderAsync(token);
        DateTimeOffset? Time(int column) => reader.IsDBNull(column) ? null : reader.GetFieldValue<DateTimeOffset>(column);
        long? Revision(int column) => reader.IsDBNull(column) ? null : reader.GetInt64(column);
        string? Text(int column) => reader.IsDBNull(column) ? null : reader.GetString(column);
        var plans = new Dictionary<Guid, ManagedPlanStatus>();
        while (await reader.ReadAsync(token))
        {
            var heartbeat = Time(7);
            var monitored = Time(10);
            var fromMonitor = monitored is not null && (heartbeat is null || monitored > heartbeat);
            var heartbeatReady = heartbeat >= cutoff ? reader.GetBoolean(8) : (bool?)null;
            var monitorUnavailable = monitored >= cutoff && !reader.IsDBNull(13) && !reader.GetBoolean(13);
            var executorSignal = heartbeatReady != false && monitorUnavailable ? monitored : heartbeat;
            plans.Add(reader.GetGuid(0), new(reader.GetInt64(1), Revision(4), Time(5), Text(2), Text(3),
                fromMonitor ? Revision(9) : Revision(6), fromMonitor ? monitored : heartbeat,
                monitorUnavailable ? false : heartbeatReady, executorSignal,
                Revision(9), monitored, reader.GetInt32(11), reader.GetInt32(12), cutoff));
        }
        return plans;
    }
}
