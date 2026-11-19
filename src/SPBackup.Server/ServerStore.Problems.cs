namespace SPBackup.Server;

public sealed record AgentProblem(Guid AgentId, string AgentName, Guid? JobId, string? JobName, string Code,
    DateTimeOffset? LastSeenUtc, DateTimeOffset? ObservedUtc)
{
    public Guid? RunId { get; init; }
    public DateTimeOffset? RunFinishedUtc { get; init; }
}
public sealed record AgentProblemPage(IReadOnlyList<AgentProblem> Items, int Page, int PageSize, long Total)
{
    public long PageItems { get; init; }
    public long AffectedAgents { get; init; }
    public long AffectedJobs { get; init; }
}
public sealed partial class ServerStore
{
    private const string ProblemRowsSql = """
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
                WHERE NOT a.revoked AND (@agent_ids::uuid[] IS NULL OR a.id=ANY(@agent_ids::uuid[])) AND (@folder::uuid IS NULL OR a.folder_id IN (SELECT id FROM selected_folders))
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
                UNION ALL SELECT id,name,(j->>'jobId')::uuid,j->>'name',run->>'status',seen,observed FROM jobs
                    WHERE run->>'status' IN ('Failed','Cancelled','Interrupted')
                UNION ALL SELECT id,name,(j->>'jobId')::uuid,j->>'name','Partial',seen,observed FROM jobs
                    WHERE run->>'status'='PartiallySucceeded'
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
            )
            """;

    public async Task<AgentProblemPage> ProblemsAsync(int page, string? kind, string? search, Guid? folder, bool root,
        int offlineSeconds, CancellationToken token, Guid? agentId = null, bool groupByJob = false)
    {
        if (agentId == Guid.Empty || page is < 0 or > 100000 || offlineSeconds < 1 || !ProblemText.Filters.ContainsKey(kind ?? "all")
            || search is { Length: > 128 } || search?.Any(char.IsControl) == true)
            throw new ServerRequestException(400, "invalid_problem_filter");
        if (folder is { } id && !(await FoldersAsync(token)).Any(f => f.Id == id)) throw new ServerRequestException(404, "folder_not_found");
        await using var db = await source.OpenConnectionAsync(token);
        await using var query = Command(db, null, ProblemRowsSql + "\n" + """
            , filtered AS (
                SELECT * FROM problems WHERE (@kind='all' OR code=@kind)
                    AND (@search='' OR strpos(lower(translate(name,'АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ','абвгдеёжзийклмнопрстуфхцчшщъыьэюя')),lower(translate(@search,'АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ','абвгдеёжзийклмнопрстуфхцчшщъыьэюя')))>0 OR strpos(lower(translate(coalesce(job_name,''),'АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ','абвгдеёжзийклмнопрстуфхцчшщъыьэюя')),lower(translate(@search,'АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ','абвгдеёжзийклмнопрстуфхцчшщъыьэюя')))>0)
            )
            , prioritized AS (
                SELECT f.*, min(priority) OVER (PARTITION BY id,job_id) group_priority
                FROM (SELECT *, CASE code WHEN 'Offline' THEN 0 WHEN 'Failed' THEN 1 WHEN 'Interrupted' THEN 1
                    WHEN 'Partial' THEN 2 WHEN 'Incomplete' THEN 3 ELSE 4 END priority FROM filtered) f
            ), ranked AS (
                SELECT prioritized.*,
                    row_number() OVER (ORDER BY priority,name,id,job_id,code) row_index,
                    dense_rank() OVER (ORDER BY group_priority,name,id,job_id NULLS FIRST) group_index
                FROM prioritized
            ), counted AS (
                SELECT count(*) total, count(DISTINCT id) agent_count,
                    count(DISTINCT (id,job_id)) FILTER (WHERE job_id IS NOT NULL) job_count,
                    coalesce(max(group_index),0) group_count FROM ranked
            )
            SELECT p.id,p.name,p.job_id,p.job_name,p.code,p.seen,p.observed,t.total,
                CASE WHEN @grouped THEN t.group_count ELSE t.total END page_items,t.agent_count,t.job_count,
                (j.run->>'runId')::uuid, coalesce(j.run#>>'{timing,observedFinishedUtc}', j.run->>'finishedUtc')::timestamptz
            FROM counted t
            LEFT JOIN LATERAL (
                SELECT * FROM ranked
                WHERE (CASE WHEN @grouped THEN group_index ELSE row_index END) BETWEEN @offset::bigint+1 AND @offset::bigint+50
                ORDER BY CASE WHEN @grouped THEN group_index ELSE row_index END,priority,code
            ) p ON true
            LEFT JOIN jobs j ON j.id=p.id AND (j.j->>'jobId')::uuid=p.job_id
                AND p.code IN ('Failed','Cancelled','Interrupted','Partial','Incomplete','Warning')
            ORDER BY CASE WHEN @grouped THEN p.group_index ELSE p.row_index END,p.priority,p.code
            """, ("agent_ids", agentId is { } agent ? new[] { agent } : null), ("folder", folder), ("root", root), ("seconds", offlineSeconds),
            ("kind", kind ?? "all"), ("search", search?.Trim() ?? ""), ("offset", page * 50), ("grouped", groupByJob));
        await using var reader = await query.ExecuteReaderAsync(token);
        var rows = new List<AgentProblem>();
        long total = 0, pageItems = 0, affectedAgents = 0, affectedJobs = 0;
        while (await reader.ReadAsync(token))
        {
            total = reader.GetInt64(7);
            pageItems = reader.GetInt64(8);
            affectedAgents = reader.GetInt64(9);
            affectedJobs = reader.GetInt64(10);
            if (!reader.IsDBNull(0)) rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6))
            {
                RunId = reader.IsDBNull(11) ? null : reader.GetGuid(11),
                RunFinishedUtc = reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12)
            });
        }
        return new(rows, page, 50, total) { PageItems = pageItems, AffectedAgents = affectedAgents, AffectedJobs = affectedJobs };
    }
}
