namespace SPBackup.Server;

public sealed record AgentProblemCount(string Code, long Count);
public sealed record AgentProblemSummary(Guid AgentId, DateTimeOffset? ObservedUtc, IReadOnlyList<AgentProblemCount> Reasons)
{
    public long Total => Reasons.Sum(reason => reason.Count);
}

public sealed partial class ServerStore
{
    // Restrict the shared problem query to the visible page before expanding jobs/runs.
    public async Task<IReadOnlyList<AgentProblemSummary>> ProblemSummariesAsync(IReadOnlyList<Guid> agentIds,
        int offlineSeconds, CancellationToken token)
    {
        if (agentIds.Count > 50 || agentIds.Contains(Guid.Empty) || offlineSeconds < 1)
            throw new ServerRequestException(400, "invalid_problem_filter");
        if (agentIds.Count == 0) return [];
        await using var db = await source.OpenConnectionAsync(token);
        await using var query = Command(db, null, ProblemRowsSql + "\n" + """
            SELECT a.id,a.observed,p.code,count(p.code) FROM agents a
            LEFT JOIN problems p ON p.id=a.id
            GROUP BY a.id,a.observed,p.code ORDER BY a.id,p.code
            """, ("agent_ids", agentIds.Distinct().ToArray()), ("folder", null), ("root", false), ("seconds", offlineSeconds));
        await using var reader = await query.ExecuteReaderAsync(token);
        var rows = new List<AgentProblemSummary>();
        Guid? current = null; List<AgentProblemCount> reasons = [];
        while (await reader.ReadAsync(token))
        {
            var id = reader.GetGuid(0);
            if (id != current)
            {
                current = id; reasons = [];
                rows.Add(new(id, reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1), reasons));
            }
            if (!reader.IsDBNull(2)) reasons.Add(new(reader.GetString(2), reader.GetInt64(3)));
        }
        return rows;
    }
}
