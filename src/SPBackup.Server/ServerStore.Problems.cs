namespace SPBackup.Server;

public sealed record AgentProblem(Guid AgentId, string AgentName, Guid? JobId, string? JobName, string Code,
    DateTimeOffset? LastSeenUtc, DateTimeOffset? ObservedUtc);
public sealed record AgentProblemPage(IReadOnlyList<AgentProblem> Items, int Page, int PageSize, long Total);
public sealed partial class ServerStore
{
    public async Task<AgentProblemPage> ProblemsAsync(int page, string? kind, string? search, Guid? folder, bool root,
        int offlineSeconds, CancellationToken token)
    {
        if (page is < 0 or > 100000 || offlineSeconds < 1 || !ProblemText.Filters.ContainsKey(kind ?? "all")
            || search is { Length: > 128 } || search?.Any(char.IsControl) == true)
            throw new ServerRequestException(400, "invalid_problem_filter");
        if (folder is { } id && !(await FoldersAsync(token)).Any(f => f.Id == id)) throw new ServerRequestException(404, "folder_not_found");
        await using var db = await source.OpenConnectionAsync(token);
        await using var query = Command(db, null, """
            WITH RECURSIVE selected_folders AS (
                SELECT id FROM server_agent_folders WHERE id=@folder::uuid
                UNION ALL SELECT f.id FROM server_agent_folders f JOIN selected_folders p ON f.parent_id=p.id
            ), agents AS (
                SELECT a.id,a.name,greatest(a.received_utc,m.received_utc) seen,m.received_utc observed,m.document,
                    c.revision desired,o.status decision
                FROM server_agents a LEFT JOIN server_monitoring m ON m.agent_id=a.id
                JOIN server_configurations c ON c.agent_id=a.id
                LEFT JOIN server_configuration_offers o ON o.agent_id=a.id AND o.generation=a.token_hash AND o.revision=c.revision
                    AND o.document_hash=sha256(convert_to(c.document::text,'UTF8'))
                WHERE NOT a.revoked AND (@folder::uuid IS NULL OR a.folder_id IN (SELECT id FROM selected_folders))
                    AND (NOT @root OR a.folder_id IS NULL)
            ), jobs AS (
                SELECT a.*,j,latest.run,successful.finished
                FROM agents a CROSS JOIN LATERAL jsonb_array_elements(a.document->'jobs') j
                LEFT JOIN LATERAL (SELECT r.document run FROM server_runs r WHERE r.agent_id=a.id AND r.job_id=(j->>'jobId')::uuid ORDER BY r.finished_utc DESC,r.run_id LIMIT 1) latest ON true
                LEFT JOIN LATERAL (SELECT r.finished_utc finished FROM server_runs r WHERE r.agent_id=a.id AND r.job_id=(j->>'jobId')::uuid
                    AND r.document->>'status'='Succeeded' AND NOT (r.document->>'incomplete')::boolean
                    AND (j->>'backupScope' IS NULL OR r.document->>'backupScope'=j->>'backupScope') ORDER BY r.finished_utc DESC,r.run_id LIMIT 1) successful ON true
                WHERE coalesce(j->>'taskKind','Backup')='Backup'
            ), problems AS (
                SELECT id,name,NULL::uuid job_id,NULL::text job_name,'Offline' code,seen,observed FROM agents
                    WHERE seen<now()-make_interval(secs=>@seconds)
                UNION ALL SELECT id,name,NULL,NULL,'Unreported',seen,observed FROM agents WHERE document IS NULL
                UNION ALL SELECT id,name,NULL,NULL,'ExecutorUnavailable',seen,observed FROM agents
                    WHERE document->>'executionMode'='Managed' AND (document->>'backupExecutionAvailable')::boolean=false
                UNION ALL SELECT id,name,NULL,NULL,CASE WHEN decision='Rejected' THEN 'PlanRejected' ELSE 'PlanPending' END,seen,observed FROM agents
                    WHERE document->>'executionMode'='Managed' AND (decision='Rejected' OR coalesce((document->>'configurationRevision')::bigint,0)<>desired)
                UNION ALL SELECT id,name,(j->>'jobId')::uuid,j->>'name','Failed',seen,observed FROM jobs
                    WHERE run->>'status' IN ('Failed','PartiallySucceeded','Cancelled','Interrupted')
                UNION ALL SELECT id,name,(j->>'jobId')::uuid,j->>'name','Incomplete',seen,observed FROM jobs WHERE (run->>'incomplete')::boolean
                UNION ALL SELECT id,name,(j->>'jobId')::uuid,j->>'name','Warning',seen,observed FROM jobs WHERE (run->>'warningCount')::int>0
                UNION ALL SELECT id,name,(j->>'jobId')::uuid,j->>'name','Overdue',seen,observed FROM jobs
                    WHERE (j->>'enabled')::boolean AND (j->>'maximumBackupAgeHours')::int>0
                      AND (finished IS NULL OR finished<now()-make_interval(hours=>(j->>'maximumBackupAgeHours')::int))
                UNION ALL SELECT id,name,(j->>'jobId')::uuid,j->>'name','AccessRequired',seen,observed FROM jobs WHERE (j->>'localAccessReady')::boolean=false
                UNION ALL SELECT a.id,a.name,(s->>'monitorId')::uuid,s->>'name',
                    CASE WHEN (s->>'failed')::boolean THEN 'PollFailed' WHEN s->>'observationStatus'='BackupFailed' THEN 'Failed' ELSE 'Overdue' END,a.seen,a.observed
                    FROM agents a CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(a.document->'sources')='array' THEN a.document->'sources' ELSE '[]'::jsonb END) s
                    WHERE (s->>'enabled')::boolean AND ((s->>'failed')::boolean OR s->>'observationStatus'='BackupFailed'
                        OR (s->>'freshnessWarning')::boolean OR (s->>'maximumBackupAgeHours')::int>0 AND
                            (s->>'lastSuccessfulUtc' IS NULL OR (s->>'lastSuccessfulUtc')::timestamptz<now()-make_interval(hours=>(s->>'maximumBackupAgeHours')::int)))
            ), filtered AS (
                SELECT * FROM problems WHERE (@kind='all' OR code=@kind)
                    AND (@search='' OR strpos(lower(translate(name,'АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ','абвгдеёжзийклмнопрстуфхцчшщъыьэюя')),lower(translate(@search,'АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ','абвгдеёжзийклмнопрстуфхцчшщъыьэюя')))>0 OR strpos(lower(translate(coalesce(job_name,''),'АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ','абвгдеёжзийклмнопрстуфхцчшщъыьэюя')),lower(translate(@search,'АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ','абвгдеёжзийклмнопрстуфхцчшщъыьэюя')))>0)
            )
            SELECT p.id,p.name,p.job_id,p.job_name,p.code,p.seen,p.observed,t.total FROM (SELECT count(*) total FROM filtered) t
            LEFT JOIN LATERAL (SELECT * FROM filtered ORDER BY CASE code WHEN 'Offline' THEN 0 WHEN 'Failed' THEN 1 WHEN 'Incomplete' THEN 2 ELSE 3 END,name,id,job_id,code LIMIT 50 OFFSET @offset) p ON true
            """, ("folder", folder), ("root", root), ("seconds", offlineSeconds), ("kind", kind ?? "all"), ("search", search?.Trim() ?? ""), ("offset", page * 50));
        await using var reader = await query.ExecuteReaderAsync(token);
        var rows = new List<AgentProblem>(); long total = 0;
        while (await reader.ReadAsync(token))
        {
            total = reader.GetInt64(7);
            if (!reader.IsDBNull(0)) rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6)));
        }
        return new(rows, page, 50, total);
    }
}
