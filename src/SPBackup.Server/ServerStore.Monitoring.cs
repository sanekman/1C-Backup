using System.Text.Json;
using SPBackup.Server.Monitoring;
namespace SPBackup.Server;
public sealed partial class ServerStore
{
    private static readonly JsonSerializerOptions MonitoringJson = new(JsonSerializerDefaults.Web);
    public sealed record Overview(int Agents, int Offline, int Unreported, int Failed, int Incomplete, int Warning,
        long Pending, long Rejected, long Unprepared, long LegacyPending);
    public async Task<Overview> OverviewAsync(int offlineSeconds,CancellationToken token)
    {
        await using var db=await source.OpenConnectionAsync(token);
        await using var cmd=Command(db,null,"""
            WITH latest AS (SELECT DISTINCT ON(agent_id,job_id) document FROM server_runs WHERE agent_id IN (SELECT id FROM server_agents WHERE NOT revoked) ORDER BY agent_id,job_id,finished_utc DESC,run_id),
            agents AS (SELECT a.id, greatest(a.received_utc,m.received_utc) seen,m.document FROM server_agents a LEFT JOIN server_monitoring m ON m.agent_id=a.id WHERE NOT a.revoked),
            delivery AS (
                SELECT (document->>'pendingRuns')::integer pending,
                    coalesce((document#>>'{reportRejections,invalid}')::integer,0)
                    + coalesce((document#>>'{reportRejections,conflict}')::integer,0)
                    + coalesce((document#>>'{reportRejections,unknown}')::integer,0) rejected,
                    (document->>'collectionWarnings')::integer unprepared,
                    document->'reportRejections' IS NOT NULL AS has_breakdown
                FROM agents
            )
            SELECT (SELECT count(*)::integer FROM agents),
              (SELECT count(*)::integer FROM agents WHERE seen IS NOT NULL AND seen<now()-make_interval(secs=>@seconds)),
              (SELECT count(*)::integer FROM agents WHERE document IS NULL),
              (SELECT count(*)::integer FROM latest WHERE document->>'status' IN ('Failed','Interrupted','Cancelled','PartiallySucceeded')),
              (SELECT count(*)::integer FROM latest WHERE (document->>'incomplete')::boolean),
              (SELECT count(*)::integer FROM latest WHERE (document->>'warningCount')::integer>0),
              (SELECT coalesce(sum(pending - rejected) FILTER (WHERE has_breakdown),0) FROM delivery),
              (SELECT coalesce(sum(rejected),0) FROM delivery),
              (SELECT coalesce(sum(unprepared),0) FROM delivery),
              (SELECT coalesce(sum(pending) FILTER (WHERE NOT has_breakdown),0) FROM delivery)
            """,("seconds",offlineSeconds));
        await using var reader=await cmd.ExecuteReaderAsync(token); await reader.ReadAsync(token);
        return new(reader.GetInt32(0),reader.GetInt32(1),reader.GetInt32(2),reader.GetInt32(3),reader.GetInt32(4),reader.GetInt32(5),
            reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9));
    }
    public async Task<IReadOnlyList<MonitoredRun>> LastSuccessfulRunsAsync(Guid id,CancellationToken token)
    {
        await using var db=await source.OpenConnectionAsync(token);
        await using var cmd=Command(db,null,"""
            SELECT DISTINCT ON(r.job_id) r.document::text
            FROM server_runs r
            WHERE r.agent_id=@id AND r.document->>'status'='Succeeded' AND (r.document->>'incomplete')::boolean=false
              AND NOT EXISTS(
                SELECT 1 FROM server_monitoring m CROSS JOIN LATERAL jsonb_array_elements(m.document->'jobs') j
                WHERE m.agent_id=r.agent_id AND j->>'jobId'=r.job_id::text AND j->>'backupScope' IS NOT NULL
                  AND j->>'backupScope' IS DISTINCT FROM r.document->>'backupScope')
            ORDER BY r.job_id,r.finished_utc DESC,r.run_id
            """,("id",id));
        await using var reader=await cmd.ExecuteReaderAsync(token);
        var runs=new List<MonitoredRun>();
        while(await reader.ReadAsync(token)) runs.Add(JsonSerializer.Deserialize<MonitoredRun>(reader.GetString(0),MonitoringJson)!);
        return runs;
    }
    public async Task SaveMonitoringAsync(Guid agentId, MonitoringSnapshot snapshot, CancellationToken token)
    {
        if (!MonitoringValidation.Valid(snapshot)) throw new ServerRequestException(400,"invalid_monitoring");
        await using var db = await source.OpenConnectionAsync(token);
        await using var cmd = Command(db,null,"INSERT INTO server_monitoring(agent_id,document) VALUES(@id,@doc::jsonb) ON CONFLICT(agent_id) DO UPDATE SET document=excluded.document,received_utc=now()",
            ("id",agentId),("doc",JsonSerializer.Serialize(snapshot,MonitoringJson)));
        await cmd.ExecuteNonQueryAsync(token);
    }
    public async Task SaveRunAsync(Guid agentId, MonitoredRun run, CancellationToken token)
    {
        if (!MonitoringValidation.Valid(run)) throw new ServerRequestException(400,"invalid_run_report");
        await using var db = await source.OpenConnectionAsync(token);
        // A retry can fill timing omitted by a legacy peer. The result and known timing stay immutable.
        await using var cmd = Command(db, null, """
            INSERT INTO server_runs(agent_id,run_id,job_id,finished_utc,document)
            VALUES(@id,@run,@job,@finished,@doc::jsonb)
            ON CONFLICT(agent_id,run_id) DO UPDATE SET document=server_runs.document||excluded.document
            WHERE server_runs.document - 'timing'=excluded.document - 'timing'
                AND (NOT (server_runs.document ? 'timing') OR NOT (excluded.document ? 'timing')
                    OR server_runs.document->'timing'=excluded.document->'timing')
            """,
            ("id",agentId),("run",run.RunId),("job",run.JobId),("finished",run.FinishedUtc),("doc",JsonSerializer.Serialize(run,MonitoringJson)));
        if (await cmd.ExecuteNonQueryAsync(token)!=1) throw new ServerRequestException(409,"run_report_conflict");
    }
    public async Task<MonitoringState?> MonitoringAsync(Guid id, CancellationToken token)
    {
        await using var db = await source.OpenConnectionAsync(token);
        await using var cmd = Command(db,null,"SELECT received_utc,document::text FROM server_monitoring WHERE agent_id=@id",("id",id));
        await using var reader = await cmd.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? new(reader.GetFieldValue<DateTimeOffset>(0),JsonSerializer.Deserialize<MonitoringSnapshot>(reader.GetString(1),MonitoringJson)!) : null;
    }
    public async Task<MonitoredRunPage> RunsAsync(Guid id, int page, CancellationToken token, bool agentCopiesOnly = false)
    {
        if (page is <0 or >100000) throw new ServerRequestException(400,"invalid_page");
        await using var db = await source.OpenConnectionAsync(token);
        await using var cmd = Command(db,null,"SELECT document::text FROM server_runs WHERE agent_id=@id AND (NOT @copies OR document->>'evidence' IS NULL) ORDER BY finished_utc DESC,run_id LIMIT 50 OFFSET @offset",("id",id),("offset",page*50),("copies",agentCopiesOnly));
        await using var reader = await cmd.ExecuteReaderAsync(token);
        var runs = new List<MonitoredRun>();
        while (await reader.ReadAsync(token)) runs.Add(JsonSerializer.Deserialize<MonitoredRun>(reader.GetString(0),MonitoringJson)!);
        return new(runs,page,50);
    }
    public async Task<IReadOnlyList<MonitoredRun>> LatestRunsAsync(Guid id, CancellationToken token)
    {
        await using var db = await source.OpenConnectionAsync(token);
        await using var cmd = Command(db,null,"SELECT DISTINCT ON(job_id) document::text FROM server_runs WHERE agent_id=@id ORDER BY job_id,finished_utc DESC,run_id",("id",id));
        await using var reader = await cmd.ExecuteReaderAsync(token);
        var runs = new List<MonitoredRun>();
        while (await reader.ReadAsync(token)) runs.Add(JsonSerializer.Deserialize<MonitoredRun>(reader.GetString(0),MonitoringJson)!);
        return runs;
    }
    public async Task<IReadOnlyList<MonitoredRun>> RunsByIdAsync(Guid id, IReadOnlyList<Guid> runIds, CancellationToken token)
    {
        if (runIds.Count > 1000 || runIds.Any(value => value == Guid.Empty)) throw new ServerRequestException(400,"invalid_run_ids");
        if (runIds.Count == 0) return [];
        await using var db = await source.OpenConnectionAsync(token);
        await using var cmd = Command(db,null,"SELECT document::text FROM server_runs WHERE agent_id=@id AND run_id=ANY(@runs)",
            ("id",id),("runs",runIds.Distinct().ToArray()));
        await using var reader = await cmd.ExecuteReaderAsync(token);
        var runs = new List<MonitoredRun>();
        while (await reader.ReadAsync(token)) runs.Add(JsonSerializer.Deserialize<MonitoredRun>(reader.GetString(0),MonitoringJson)!);
        return runs;
    }
}
