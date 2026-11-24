using Npgsql;

namespace SPBackup.Server;

public sealed record OperatorAccount(Guid Id, string Name, bool Enabled, Guid Stamp);
public sealed record OperatorGrant(Guid AgentId, Guid JobId, bool Download = false);
public sealed record OperatorChoice(OperatorGrant Grant, string AgentName, string? JobName);
public sealed record OperatorJob(Guid AgentId, Guid? JobId, string AgentName, string JobName, DateTimeOffset? SeenUtc,
    DateTimeOffset? ObservedUtc, string? Status, DateTimeOffset? FinishedUtc);

public sealed record OperatorJobHistory(string AgentName, string JobName, IReadOnlyList<Monitoring.MonitoredRun> Runs);

public sealed partial class ServerStore
{
    public async Task<IReadOnlyList<OperatorAccount>> OperatorsAsync(CancellationToken token)
    {
        await using var db = await source.OpenConnectionAsync(token);
        await using var command = Command(db, null, "SELECT id,name,enabled,security_stamp FROM server_users WHERE role='Operator' ORDER BY name");
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<OperatorAccount>();
        while (await reader.ReadAsync(token)) result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetBoolean(2), reader.GetGuid(3)));
        return result;
    }

    public async Task<IReadOnlyList<OperatorChoice>> OperatorChoicesAsync(CancellationToken token)
    {
        await using var db = await source.OpenConnectionAsync(token);
        await using var command = Command(db, null, """
            SELECT a.id,a.name,'00000000-0000-0000-0000-000000000000'::uuid,NULL::text FROM server_agents a WHERE NOT a.revoked
            UNION ALL
            SELECT a.id,a.name,(j->>'jobId')::uuid,j->>'name' FROM server_agents a
            JOIN server_monitoring m ON m.agent_id=a.id CROSS JOIN LATERAL jsonb_array_elements(m.document->'jobs') j WHERE NOT a.revoked
            UNION ALL
            SELECT a.id,a.name,r.job_id,r.document->>'jobName'
            FROM server_agents a LEFT JOIN server_monitoring m ON m.agent_id=a.id
            JOIN LATERAL (
                SELECT DISTINCT ON (job_id) job_id,document FROM server_runs WHERE agent_id=a.id
                ORDER BY job_id,finished_utc DESC,run_id
            ) r ON true
            WHERE NOT a.revoked AND NOT EXISTS (
                SELECT 1 FROM jsonb_array_elements(m.document->'jobs') j WHERE (j->>'jobId')::uuid=r.job_id)
            ORDER BY 2,1,4 NULLS FIRST
            """);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<OperatorChoice>();
        while (await reader.ReadAsync(token)) result.Add(new(new(reader.GetGuid(0), reader.GetGuid(2)), reader.GetString(1),
            reader.IsDBNull(3) ? null : reader.GetString(3)));
        return result;
    }

    public async Task<IReadOnlyList<OperatorGrant>> OperatorGrantsAsync(Guid id, CancellationToken token)
    {
        await using var db = await source.OpenConnectionAsync(token);
        await using var command = Command(db, null, "SELECT agent_id,job_id,allow_download FROM server_operator_grants WHERE user_id=@id", ("id", id));
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<OperatorGrant>();
        while (await reader.ReadAsync(token)) result.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetBoolean(2)));
        return result;
    }

    public async Task<Guid> SaveOperatorAsync(Guid administrator, Guid? id, Guid? expectedStamp, string? name, string? password,
        bool enabled, IReadOnlyList<OperatorGrant> grants, CancellationToken token)
    {
        var create = id is null;
        name = name?.Trim().ToLowerInvariant();
        if (grants.Count > 500 || grants.Any(grant => grant.AgentId == Guid.Empty)
            || create && (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl) || string.IsNullOrEmpty(password))
            || !create && (id == Guid.Empty || expectedStamp is null || expectedStamp == Guid.Empty)
            || password is not null && (password.Length is < 12 or > 256 || password.Any(char.IsControl)))
            throw new ServerRequestException(400, "invalid_operator");
        var userId = id ?? Guid.NewGuid();
        var stamp = Guid.NewGuid();
        var hash = password is null ? null : passwords.HashPassword(new ServerUser(userId, name ?? "", "", stamp), password);
        await using var db = await source.OpenConnectionAsync(token);
        await using var tx = await db.BeginTransactionAsync(token);
        if (create)
        {
            await using var insert = Command(db, tx, """
                INSERT INTO server_users(id,name,password_hash,security_stamp,role,enabled)
                VALUES(@id,@name,@hash,@stamp,'Operator',@enabled) ON CONFLICT(name) DO NOTHING
                """, ("id", userId), ("name", name), ("hash", hash), ("stamp", stamp), ("enabled", enabled));
            if (await insert.ExecuteNonQueryAsync(token) != 1) throw new ServerRequestException(409, "operator_name_exists");
        }
        else
        {
            await using var update = Command(db, tx, """
                UPDATE server_users SET enabled=@enabled,password_hash=coalesce(@hash::text,password_hash),security_stamp=@stamp
                WHERE id=@id AND role='Operator' AND security_stamp=@expected
                """, ("id", userId), ("expected", expectedStamp), ("enabled", enabled), ("hash", hash), ("stamp", stamp));
            if (await update.ExecuteNonQueryAsync(token) != 1) throw new ServerRequestException(409, "operator_changed");
        }
        await using (var clear = Command(db, tx, "DELETE FROM server_operator_grants WHERE user_id=@id", ("id", userId)))
            await clear.ExecuteNonQueryAsync(token);
        var distinct = grants.GroupBy(grant => (grant.AgentId, grant.JobId))
            .Select(group => new OperatorGrant(group.Key.AgentId, group.Key.JobId, group.Any(grant => grant.Download))).ToArray();
        await using (var insert = Command(db, tx, """
            INSERT INTO server_operator_grants(user_id,agent_id,job_id,allow_download)
            SELECT @id,g.agent,g.job,g.download FROM unnest(@agents::uuid[],@jobs::uuid[],@downloads::boolean[]) AS g(agent,job,download)
            JOIN server_agents a ON a.id=g.agent AND NOT a.revoked LEFT JOIN server_monitoring m ON m.agent_id=a.id
            WHERE g.job='00000000-0000-0000-0000-000000000000'
                OR EXISTS (SELECT 1 FROM jsonb_array_elements(m.document->'jobs') j WHERE (j->>'jobId')::uuid=g.job)
                OR EXISTS (SELECT 1 FROM server_runs r WHERE r.agent_id=g.agent AND r.job_id=g.job)
            """, ("id", userId), ("agents", distinct.Select(grant => grant.AgentId).ToArray()),
            ("jobs", distinct.Select(grant => grant.JobId).ToArray()), ("downloads", distinct.Select(grant => grant.Download).ToArray())))
            if (await insert.ExecuteNonQueryAsync(token) != distinct.Length) throw new ServerRequestException(409, "operator_scope_changed");
        await AuditAsync(db, tx, administrator, null, create ? "OperatorCreated" : "OperatorUpdated", token);
        await tx.CommitAsync(token);
        return userId;
    }

    private const string ActiveOperator = """
        SELECT g.agent_id,g.job_id FROM server_operator_grants g
        JOIN server_users u ON u.id=g.user_id AND u.enabled AND u.role='Operator'
        WHERE g.user_id=@id
        """;

    private const string AllJobs = "'00000000-0000-0000-0000-000000000000'::uuid";

    public async Task<IReadOnlyList<AgentFolderOption>> OperatorFoldersAsync(Guid id, CancellationToken token)
    {
        await using var db = await source.OpenConnectionAsync(token);
        await using var cmd = Command(db, null, $$"""
            WITH RECURSIVE grants AS ({{ActiveOperator}}), visible(id,parent_id,name) AS (
                SELECT f.id,f.parent_id,f.name FROM server_agent_folders f
                JOIN server_agents a ON a.folder_id=f.id AND NOT a.revoked
                WHERE EXISTS (SELECT 1 FROM grants g WHERE g.agent_id=a.id)
                UNION
                SELECT f.id,f.parent_id,f.name FROM server_agent_folders f JOIN visible v ON v.parent_id=f.id
            )
            SELECT id,parent_id,name FROM visible ORDER BY name,id
            """, ("id", id));
        await using var reader = await cmd.ExecuteReaderAsync(token);
        var folders = new List<AgentFolderOption>();
        while (await reader.ReadAsync(token))
            folders.Add(new(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetString(2), "", 0));
        return folders;
    }

    public async Task<IReadOnlyList<OperatorJob>> OperatorJobsAsync(Guid id, string? search, int page,
        CancellationToken token, Guid? folder = null, bool root = false)
    {
        if (page is < 0 or > 10000 || search is { Length: > 128 } || search?.Any(char.IsControl) == true)
            throw new ServerRequestException(400, "invalid_operator_filter");
        await using var db = await source.OpenConnectionAsync(token);
        await using var command = Command(db, null, $$"""
            WITH RECURSIVE grants AS ({{ActiveOperator}}), branch(id) AS (
                SELECT @folder::uuid WHERE @folder::uuid IS NOT NULL UNION
                SELECT f.id FROM server_agent_folders f JOIN branch b ON f.parent_id=b.id
            )
            SELECT a.id,j.id,a.name,j.name,greatest(a.received_utc,m.received_utc),m.received_utc,
                   CASE WHEN r.document->>'status'='Succeeded' AND (r.document->>'incomplete')::boolean THEN 'Incomplete'
                        WHEN r.document->>'status'='Succeeded' AND (r.document->>'warningCount')::int>0 THEN 'Warning'
                        ELSE r.document->>'status' END,
                   coalesce((r.document#>>'{timing,observedFinishedUtc}')::timestamptz,r.finished_utc)
            FROM server_agents a LEFT JOIN server_monitoring m ON m.agent_id=a.id
            LEFT JOIN LATERAL (
                SELECT (item->>'jobId')::uuid id,item->>'name' name FROM jsonb_array_elements(m.document->'jobs') item
                UNION ALL
                SELECT historical.job_id,historical.document->>'jobName'
                FROM (
                    SELECT DISTINCT ON (h.job_id) h.job_id,h.document FROM server_runs h WHERE h.agent_id=a.id
                    ORDER BY h.job_id,h.finished_utc DESC,h.run_id
                ) historical
                WHERE NOT EXISTS (SELECT 1 FROM jsonb_array_elements(m.document->'jobs') item
                    WHERE (item->>'jobId')::uuid=historical.job_id)
            ) j ON true
            LEFT JOIN LATERAL (
                SELECT document,finished_utc FROM server_runs
                WHERE agent_id=a.id AND job_id=j.id ORDER BY finished_utc DESC,run_id LIMIT 1
            ) r ON true
            WHERE NOT a.revoked
                AND (NOT @root OR a.folder_id IS NULL)
                AND (@folder::uuid IS NULL OR a.folder_id IN (SELECT id FROM branch))
                AND EXISTS (SELECT 1 FROM grants g WHERE g.agent_id=a.id AND (g.job_id={{AllJobs}} OR g.job_id=j.id))
                AND (@search='' OR strpos(lower(a.name),lower(@search)) > 0 OR strpos(lower(a.computer_name),lower(@search)) > 0
                    OR strpos(lower(j.name),lower(@search)) > 0)
            ORDER BY a.name,a.id,j.name,j.id LIMIT 51 OFFSET @offset
            """, ("id", id), ("search", search?.Trim() ?? ""), ("offset", page * 50), ("folder", folder), ("root", root));
        await using var reader = await command.ExecuteReaderAsync(token);
        DateTimeOffset? Time(int column) => reader.IsDBNull(column) ? null : reader.GetFieldValue<DateTimeOffset>(column);
        var rows = new List<OperatorJob>();
        while (await reader.ReadAsync(token))
            rows.Add(new(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetString(2),
                reader.IsDBNull(3) ? "Ожидается отчёт о заданиях" : reader.GetString(3),
                Time(4), Time(5), reader.IsDBNull(6) ? null : reader.GetString(6), Time(7)));
        return rows;
    }

    public async Task<OperatorJobHistory?> OperatorHistoryAsync(Guid id, Guid agent, Guid job, Guid? run, int page, CancellationToken token)
    {
        if (agent == Guid.Empty || job == Guid.Empty || run == Guid.Empty || page is < 0 or > 10000) return null;
        await using var db = await source.OpenConnectionAsync(token);
        await using var cmd = Command(db, null, $$"""
            WITH grants AS ({{ActiveOperator}})
            SELECT a.name,coalesce(j.name,h.name),result.document::text
            FROM server_agents a LEFT JOIN server_monitoring m ON m.agent_id=a.id
            LEFT JOIN LATERAL (
                SELECT item->>'name' name FROM jsonb_array_elements(m.document->'jobs') item WHERE (item->>'jobId')::uuid=@job LIMIT 1
            ) j ON true
            LEFT JOIN LATERAL (
                SELECT document->>'jobName' name FROM server_runs WHERE agent_id=a.id AND job_id=@job ORDER BY finished_utc DESC,run_id LIMIT 1
            ) h ON true
            LEFT JOIN LATERAL (
                SELECT document FROM server_runs WHERE agent_id=a.id AND job_id=@job AND (@run::uuid IS NULL OR run_id=@run)
                ORDER BY finished_utc DESC,run_id LIMIT 51 OFFSET @offset
            ) result ON true
            WHERE a.id=@agent AND NOT a.revoked AND coalesce(j.name,h.name) IS NOT NULL
                AND (@run::uuid IS NULL OR result.document IS NOT NULL)
                AND EXISTS (SELECT 1 FROM grants g WHERE g.agent_id=a.id AND (g.job_id={{AllJobs}} OR g.job_id=@job))
            """, ("id", id), ("agent", agent), ("job", job), ("run", run), ("offset", run.HasValue ? 0 : page * 50));
        await using var reader = await cmd.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var agentName = reader.GetString(0);
        var jobName = reader.GetString(1);
        var runs = new List<Monitoring.MonitoredRun>();
        do
        {
            if (!reader.IsDBNull(2)) runs.Add(System.Text.Json.JsonSerializer.Deserialize<Monitoring.MonitoredRun>(reader.GetString(2), MonitoringJson)!);
        } while (await reader.ReadAsync(token));
        return new(agentName, jobName, runs);
    }

    internal async Task<byte[]?> DownloadAuthorityAsync(Guid user, Guid stamp, Guid agent, Guid job, Guid run, int destination,
        CancellationToken token)
    {
        await using var db = await source.OpenConnectionAsync(token);
        await using var command = Command(db, null, $$"""
            SELECT a.token_hash FROM server_agents a JOIN server_runs r ON r.agent_id=a.id
            JOIN server_users u ON u.id=@user AND u.enabled AND u.security_stamp=@stamp
            JOIN server_monitoring m ON m.agent_id=a.id AND m.document->>'recoveryKitExportAvailable'='true'
            WHERE a.id=@agent AND NOT a.revoked AND r.job_id=@job AND r.run_id=@run
                AND r.document->>'evidence' IS NULL AND r.document->>'copyKind' IN ('Full','Incremental')
                AND EXISTS (SELECT 1 FROM jsonb_array_elements(r.document->'destinations') d
                    WHERE d->>'succeeded'='true' AND (d->>'index')::integer=@destination)
                AND (u.role='Administrator' OR (u.role='Operator' AND EXISTS (
                    SELECT 1 FROM server_operator_grants g WHERE g.user_id=u.id AND g.allow_download
                        AND g.agent_id=a.id AND (g.job_id={{AllJobs}} OR g.job_id=@job))))
            """, ("user", user), ("stamp", stamp), ("agent", agent), ("job", job), ("run", run), ("destination", destination));
        return await command.ExecuteScalarAsync(token) as byte[];
    }

    internal async Task RecordDownloadAsync(Guid user, Guid agent, string action, CancellationToken token)
    {
        await using var db = await source.OpenConnectionAsync(token);
        await using var tx = await db.BeginTransactionAsync(token);
        await AuditAsync(db, tx, user, agent, action, token);
        await tx.CommitAsync(token);
    }
}
