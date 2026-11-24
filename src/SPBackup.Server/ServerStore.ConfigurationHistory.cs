namespace SPBackup.Server;

public sealed record ConfigurationHistoryEntry(long Revision, DateTimeOffset SavedUtc, string? Author, long? RestoredFrom, int JobCount);
public sealed record ConfigurationHistoryPage(IReadOnlyList<ConfigurationHistoryEntry> Entries, int Total, int Page);

public sealed partial class ServerStore
{
    public async Task<ConfigurationHistoryPage> FileConfigurationHistoryAsync(Guid agent, int page, CancellationToken token)
    {
        page = Math.Clamp(page, 0, 4);
        await using var db = await source.OpenConnectionAsync(token);
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, token);
        await using (var check = Command(db, tx, "SELECT 1 FROM server_agents WHERE id=@id", ("id", agent)))
            if (await check.ExecuteScalarAsync(token) is null) throw new ServerRequestException(404, "agent_not_found");
        int total;
        await using (var count = Command(db, tx, "SELECT count(*)::integer FROM server_configuration_history WHERE agent_id=@id", ("id", agent)))
            total = (int)(await count.ExecuteScalarAsync(token))!;
        var entries = new List<ConfigurationHistoryEntry>();
        await using (var query = Command(db, tx, """
            SELECT h.revision,h.saved_utc,u.name,h.restored_from,
                CASE WHEN jsonb_typeof(h.document->'jobs')='array' THEN jsonb_array_length(h.document->'jobs') ELSE 0 END
            FROM server_configuration_history h LEFT JOIN server_users u ON u.id=h.author_id
            WHERE h.agent_id=@id ORDER BY h.revision DESC LIMIT 20 OFFSET @offset
            """, ("id", agent), ("offset", page * 20)))
        await using (var reader = await query.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) entries.Add(new(reader.GetInt64(0), reader.GetFieldValue<DateTimeOffset>(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.GetInt32(4)));
        await tx.CommitAsync(token);
        return new(entries, total, page);
    }

    public async Task<ManagedFileConfiguration> ReadFileConfigurationRevisionAsync(Guid agent, long revision, CancellationToken token)
    {
        await using var db = await source.OpenConnectionAsync(token);
        await using var query = Command(db, null, "SELECT document::text FROM server_configuration_history WHERE agent_id=@id AND revision=@revision",
            ("id", agent), ("revision", revision));
        if (await query.ExecuteScalarAsync(token) is not string saved) throw new ServerRequestException(404, "configuration_history_not_found");
        return Editable(agent, revision, saved);
    }
}
