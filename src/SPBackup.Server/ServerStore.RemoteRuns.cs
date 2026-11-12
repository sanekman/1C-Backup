using System.Security.Cryptography;
using System.Text.Json;

namespace SPBackup.Server;

public sealed partial class ServerStore
{
    private const string RemoteColumns = "command_id,job_id,revision,expires_utc,status,run_id,reason,created_utc,updated_utc";
    private static RemoteRunState ReadRemote(Npgsql.NpgsqlDataReader r) => new(new(r.GetGuid(0), r.GetGuid(1), r.GetInt64(2), r.GetFieldValue<DateTimeOffset>(3)),
        r.GetString(4), r.IsDBNull(5) ? null : r.GetGuid(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetFieldValue<DateTimeOffset>(7), r.GetFieldValue<DateTimeOffset>(8));

    public async Task<RemoteRunState> CreateRemoteRunAsync(Guid user, Guid stamp, Guid agent, RemoteRunRequest request, CancellationToken token)
    {
        if (request.CommandId == Guid.Empty || request.JobId == Guid.Empty || request.Revision < 1 || request.LifetimeMinutes is < 1 or > 1440)
            throw new ServerRequestException(400, "invalid_remote_run");
        await using var db = await source.OpenConnectionAsync(token);
        await using var tx = await db.BeginTransactionAsync(token);
        await using (var login = Command(db, tx, "SELECT security_stamp FROM server_users WHERE id=@user FOR SHARE", ("user", user)))
            if (await login.ExecuteScalarAsync(token) is not Guid current || current != stamp) throw new ServerRequestException(401, "invalid_session");
        byte[] generation;
        await using (var row = Command(db, tx, "SELECT token_hash FROM server_agents WHERE id=@agent AND NOT revoked FOR UPDATE", ("agent", agent)))
            generation = await row.ExecuteScalarAsync(token) as byte[] ?? throw new ServerRequestException(409, "agent_unavailable");
        // A repeat of the same user action returns its first result, even after a plan change or expiry.
        await using (var row = Command(db, tx, $"SELECT {RemoteColumns},generation,lifetime_minutes,agent_id FROM server_remote_runs WHERE command_id=@id", ("id", request.CommandId)))
        await using (var reader = await row.ExecuteReaderAsync(token))
            if (await reader.ReadAsync(token))
            {
                var old = ReadRemote(reader);
                if (reader.GetGuid(11) != agent || old.Command.JobId != request.JobId || old.Command.Revision != request.Revision || reader.GetInt32(10) != request.LifetimeMinutes
                    || !CryptographicOperations.FixedTimeEquals(generation, reader.GetFieldValue<byte[]>(9)))
                    throw new ServerRequestException(409, "remote_run_conflict");
                return old;
            }
        var config = await ConfigurationInTransactionAsync(db, tx, agent, token);
        if (config.Revision != request.Revision) throw new ServerRequestException(412, "configuration_revision_conflict");
        if (!config.JobIds.Contains(request.JobId)) throw new ServerRequestException(404, "job_not_found");
        await using (var monitor = Command(db, tx, "SELECT document::text FROM server_monitoring WHERE agent_id=@agent", ("agent", agent)))
        {
            var json = await monitor.ExecuteScalarAsync(token) as string;
            var snapshot = json is null ? null : JsonSerializer.Deserialize<Monitoring.MonitoringSnapshot>(json, MonitoringJson);
            if (snapshot?.ExecutionMode != "Managed" || snapshot.ConfigurationCapabilities?.Contains(AgentCapabilities.RemoteRunsV1) != true)
                throw new ServerRequestException(409, "remote_runs_unsupported");
        }
        await using (var limit = Command(db, tx, "SELECT count(*) FROM server_remote_runs WHERE agent_id=@agent AND status IN ('Queued','Accepted','Running') AND expires_utc>now()", ("agent", agent)))
            if ((long)(await limit.ExecuteScalarAsync(token))! >= 100) throw new ServerRequestException(409, "remote_queue_full");
        RemoteRunState result;
        try
        {
            await using (var insert = Command(db, tx, $"""
                INSERT INTO server_remote_runs(command_id,agent_id,generation,job_id,revision,lifetime_minutes,expires_utc)
                VALUES(@id,@agent,@generation,@job,@revision,@minutes,now()+make_interval(mins=>@minutes)) RETURNING {RemoteColumns}
                """, ("id", request.CommandId), ("agent", agent), ("generation", generation), ("job", request.JobId), ("revision", request.Revision), ("minutes", request.LifetimeMinutes)))
            await using (var reader = await insert.ExecuteReaderAsync(token)) { await reader.ReadAsync(token); result = ReadRemote(reader); }
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
        { throw new ServerRequestException(409, "remote_run_conflict"); }
        await AuditAsync(db, tx, user, agent, "RemoteRunQueued", token);
        await tx.CommitAsync(token);
        return result;
    }
    private static async Task<(long Revision, IReadOnlyList<Guid> JobIds)> ConfigurationInTransactionAsync(Npgsql.NpgsqlConnection db, Npgsql.NpgsqlTransaction tx, Guid agent, CancellationToken token)
    {
        await using var row = Command(db, tx, "SELECT revision,document::text FROM server_configurations WHERE agent_id=@agent FOR SHARE", ("agent", agent));
        await using var r = await row.ExecuteReaderAsync(token);
        if (!await r.ReadAsync(token)) throw new ServerRequestException(404, "agent_not_found");
        try
        {
            using var json = JsonDocument.Parse(r.GetString(1));
            return (r.GetInt64(0), json.RootElement.GetProperty("jobs").EnumerateArray().Select(j => j.GetProperty("id").GetGuid()).ToArray());
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new ServerRequestException(409, "configuration_format_unsupported"); }
    }
    private static async Task RemoteMaintenanceAsync(Npgsql.NpgsqlConnection db, Guid agent, CancellationToken token)
    {
        await using var update = Command(db, null, """
            UPDATE server_remote_runs c SET status=CASE WHEN (a.revoked OR c.generation<>a.token_hash) AND c.status='Running' THEN 'Interrupted' WHEN a.revoked OR c.generation<>a.token_hash THEN 'Rejected' ELSE 'Expired' END,
              reason=CASE WHEN a.revoked OR c.generation<>a.token_hash THEN 'JobUnavailable' ELSE 'DeadlineElapsed' END,updated_utc=now()
            FROM server_agents a WHERE c.agent_id=a.id AND a.id=@agent AND c.status IN ('Queued','Accepted','Running')
            AND (a.revoked OR c.generation<>a.token_hash OR c.status<>'Running' AND c.expires_utc<=now())
            """, ("agent", agent));
        await update.ExecuteNonQueryAsync(token);
    }
    public async Task<IReadOnlyList<RemoteRunState>> RemoteRunsAsync(Guid agent, CancellationToken token)
    {
        await using var db = await source.OpenConnectionAsync(token);
        await RemoteMaintenanceAsync(db, agent, token);
        await using var row = Command(db, null, $"SELECT {RemoteColumns} FROM server_remote_runs WHERE agent_id=@agent ORDER BY created_utc DESC,command_id LIMIT 100", ("agent", agent));
        await using var reader = await row.ExecuteReaderAsync(token);
        var result = new List<RemoteRunState>();
        while (await reader.ReadAsync(token)) result.Add(ReadRemote(reader));
        return result;
    }
    public async Task<IReadOnlyList<RemoteRunCommand>> PollRemoteRunsAsync(Guid agent, byte[] generation, CancellationToken token)
    {
        await using var db = await source.OpenConnectionAsync(token);
        await RemoteMaintenanceAsync(db, agent, token);
        await using var row = Command(db, null, $"""
            SELECT {RemoteColumns} FROM server_remote_runs WHERE agent_id=@agent AND generation=@generation
              AND status IN ('Queued','Accepted','Running') ORDER BY created_utc,command_id LIMIT 16
            """, ("agent", agent), ("generation", generation));
        await using var reader = await row.ExecuteReaderAsync(token);
        var result = new List<RemoteRunCommand>();
        while (await reader.ReadAsync(token)) result.Add(ReadRemote(reader).Command);
        return result;
    }
    public async Task SaveRemoteRunReceiptAsync(Guid agent, byte[] generation, RemoteRunReceipt receipt, CancellationToken token)
    {
        if (!receipt.Valid()) throw new ServerRequestException(400, "invalid_remote_receipt");
        await using var db = await source.OpenConnectionAsync(token);
        await using var tx = await db.BeginTransactionAsync(token);
        await using (var guard = Command(db, tx, "SELECT token_hash FROM server_agents WHERE id=@agent AND NOT revoked FOR SHARE", ("agent", agent)))
            if (await guard.ExecuteScalarAsync(token) is not byte[] live || !CryptographicOperations.FixedTimeEquals(live, generation))
                throw new ServerRequestException(403, "agent_connection_changed");
        (long Revision, IReadOnlyList<Guid> JobIds) config = (0, Array.Empty<Guid>());
        if (receipt.Status is "Accepted" or "Running") config = await ConfigurationInTransactionAsync(db, tx, agent, token);
        RemoteRunState old;
        await using (var row = Command(db, tx, $"SELECT {RemoteColumns} FROM server_remote_runs WHERE command_id=@id AND agent_id=@agent AND generation=@generation FOR UPDATE",
            ("id", receipt.CommandId), ("agent", agent), ("generation", generation)))
        await using (var r = await row.ExecuteReaderAsync(token))
        {
            if (!await r.ReadAsync(token)) throw new ServerRequestException(404, "remote_run_not_found");
            old = ReadRemote(r);
        }
        if (new RemoteRunReceipt(receipt.CommandId, old.Status, old.RunId, old.Reason).Terminal)
        {
            if (old.Status != receipt.Status || old.RunId != receipt.RunId || old.Reason != receipt.Reason)
                throw new ServerRequestException(409, "remote_run_final");
            return;
        }
        if (receipt.Status == old.Status)
        {
            // Start permission is single-use, unlike result delivery. A client with a lost/restored
            // local journal must not treat an old Running state as a fresh permission to execute.
            if (receipt.Status == "Running") throw new ServerRequestException(409, "remote_run_already_started");
            return;
        }
        // Delayed nonterminal receipts never undo progress or completion.
        if (old.Status == "Running" && receipt.Status == "Accepted") return;
        if (receipt.Status is "Accepted" or "Running")
        {
            if (old.Command.ExpiresUtc <= DateTimeOffset.UtcNow) throw new ServerRequestException(410, "remote_run_expired");
            if (config.Revision != old.Command.Revision || !config.JobIds.Contains(old.Command.JobId))
                throw new ServerRequestException(409, "remote_run_plan_changed");
        }
        if (!(old.Status switch {
            "Queued" => receipt.Status is "Accepted" or "Rejected" or "Expired" or "Interrupted",
            "Accepted" => receipt.Status is "Running" or "Rejected" or "Expired" or "Interrupted",
            "Running" => receipt.Status is "Succeeded" or "Failed" or "Interrupted",
            _ => false }))
            throw new ServerRequestException(409, "invalid_remote_transition");
        await using (var update = Command(db, tx, "UPDATE server_remote_runs SET status=@status,run_id=@run::uuid,reason=@reason::varchar,updated_utc=now() WHERE command_id=@id",
            ("id", receipt.CommandId), ("status", receipt.Status), ("run", receipt.RunId), ("reason", receipt.Reason)))
            await update.ExecuteNonQueryAsync(token);
        await AuditAsync(db, tx, null, agent, "RemoteRun" + receipt.Status, token);
        await tx.CommitAsync(token);
    }
}
