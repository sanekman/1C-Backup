using Npgsql;

namespace SPBackup.Server;

public sealed record OperatorAccount(Guid Id, string Name, bool Enabled, Guid Stamp);
public sealed record OperatorGrant(Guid AgentId, Guid JobId);
public sealed record OperatorChoice(OperatorGrant Grant, string AgentName, string? JobName);
public sealed record OperatorJob(Guid AgentId, string AgentName, string JobName, DateTimeOffset? SeenUtc,
    DateTimeOffset? ObservedUtc, string? Status, DateTimeOffset? FinishedUtc);

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
        await using var command = Command(db, null, "SELECT agent_id,job_id FROM server_operator_grants WHERE user_id=@id", ("id", id));
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<OperatorGrant>();
        while (await reader.ReadAsync(token)) result.Add(new(reader.GetGuid(0), reader.GetGuid(1)));
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
        var distinct = grants.Distinct().ToArray();
        await using (var insert = Command(db, tx, """
            INSERT INTO server_operator_grants(user_id,agent_id,job_id)
            SELECT @id,g.agent,g.job FROM unnest(@agents::uuid[],@jobs::uuid[]) AS g(agent,job)
            JOIN server_agents a ON a.id=g.agent AND NOT a.revoked LEFT JOIN server_monitoring m ON m.agent_id=a.id
            WHERE g.job='00000000-0000-0000-0000-000000000000'
                OR EXISTS (SELECT 1 FROM jsonb_array_elements(m.document->'jobs') j WHERE (j->>'jobId')::uuid=g.job)
            """, ("id", userId), ("agents", distinct.Select(grant => grant.AgentId).ToArray()),
            ("jobs", distinct.Select(grant => grant.JobId).ToArray())))
            if (await insert.ExecuteNonQueryAsync(token) != distinct.Length) throw new ServerRequestException(409, "operator_scope_changed");
        await AuditAsync(db, tx, administrator, null, create ? "OperatorCreated" : "OperatorUpdated", token);
        await tx.CommitAsync(token);
        return userId;
    }

    public async Task<IReadOnlyList<OperatorJob>> OperatorJobsAsync(Guid id, string? search, int page, CancellationToken token)
    {
        if (page is < 0 or > 10000 || search is { Length: > 128 } || search?.Any(char.IsControl) == true)
            throw new ServerRequestException(400, "invalid_operator_filter");
        await using var db = await source.OpenConnectionAsync(token);
        await using var command = Command(db, null, """
            SELECT a.id,a.name,j->>'name',greatest(a.received_utc,m.received_utc),m.received_utc,
                   CASE WHEN r.document->>'status'='Succeeded' AND (r.document->>'incomplete')::boolean THEN 'Incomplete'
                        WHEN r.document->>'status'='Succeeded' AND (r.document->>'warningCount')::int>0 THEN 'Warning'
                        ELSE r.document->>'status' END,
                   coalesce((r.document#>>'{timing,observedFinishedUtc}')::timestamptz,r.finished_utc)
            FROM server_agents a LEFT JOIN server_monitoring m ON m.agent_id=a.id
            LEFT JOIN LATERAL jsonb_array_elements(m.document->'jobs') j ON true
            LEFT JOIN LATERAL (
                SELECT document,finished_utc FROM server_runs
                WHERE agent_id=a.id AND job_id=(j->>'jobId')::uuid ORDER BY finished_utc DESC,run_id LIMIT 1
            ) r ON true
            WHERE NOT a.revoked AND EXISTS (
                SELECT 1 FROM server_operator_grants g JOIN server_users u ON u.id=g.user_id
                WHERE g.user_id=@id AND u.enabled AND u.role='Operator' AND g.agent_id=a.id
                    AND (g.job_id='00000000-0000-0000-0000-000000000000' OR g.job_id=(j->>'jobId')::uuid)
            ) AND (@search='' OR strpos(lower(a.name),lower(@search)) > 0 OR strpos(lower(j->>'name'),lower(@search)) > 0)
            ORDER BY a.name,a.id,j->>'name',j->>'jobId' LIMIT 51 OFFSET @offset
            """, ("id", id), ("search", search?.Trim() ?? ""), ("offset", page * 50));
        await using var reader = await command.ExecuteReaderAsync(token);
        DateTimeOffset? Time(int column) => reader.IsDBNull(column) ? null : reader.GetFieldValue<DateTimeOffset>(column);
        var rows = new List<OperatorJob>();
        while (await reader.ReadAsync(token))
            rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? "Ожидается отчёт о заданиях" : reader.GetString(2),
                Time(3), Time(4), reader.IsDBNull(5) ? null : reader.GetString(5), Time(6)));
        return rows;
    }
}
