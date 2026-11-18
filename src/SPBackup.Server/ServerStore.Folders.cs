using System.Text;
using System.Text.Json;
using SPBackup.Server.Monitoring;
using Npgsql;

namespace SPBackup.Server;

public sealed partial class ServerStore
{
    public const int MaximumFolderDepth = 16;
    public const int MaximumFolders = 1000;
    internal async Task<IReadOnlyList<AgentTreeEntry>> AgentTreeAsync(CancellationToken token)
    {
        await using var db = await source.OpenConnectionAsync(token);
        await using var cmd = Command(db, null, """
            SELECT a.id,a.folder_id,a.name,a.computer_name,a.revoked,greatest(a.received_utc,m.received_utc),m.received_utc,
                   (m.document->'progress')::text,r.document::text,
                   (SELECT j->>'name' FROM jsonb_array_elements(m.document->'jobs') AS j
                    WHERE j->>'jobId'=m.document->'progress'->>'jobId' LIMIT 1)
            FROM server_agents a LEFT JOIN server_monitoring m ON m.agent_id=a.id
            LEFT JOIN LATERAL (
                SELECT document FROM server_runs
                WHERE agent_id=a.id AND document->>'evidence' IS NULL
                ORDER BY finished_utc DESC,run_id LIMIT 1
            ) r ON true
            ORDER BY a.name,a.id
            """);
        await using var reader = await cmd.ExecuteReaderAsync(token);
        var agents = new List<AgentTreeEntry>();
        while (await reader.ReadAsync(token))
        {
            agents.Add(new(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : JsonSerializer.Deserialize<MonitoredProgress>(reader.GetString(7), MonitoringJson),
                reader.IsDBNull(8) ? null : JsonSerializer.Deserialize<MonitoredRun>(reader.GetString(8), MonitoringJson),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }
        return agents;
    }

    private static async Task OrganizationLockAsync(NpgsqlConnection db, NpgsqlTransaction tx, CancellationToken token)
    {
        await using var cmd = Command(db, tx, "SELECT pg_advisory_xact_lock(1731062103)");
        await cmd.ExecuteNonQueryAsync(token);
    }
    private static string OrganizationName(string? name, bool folder)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 128 || name.Any(char.IsControl)
            || folder && (name is "." or ".." || name.Contains('/') || name.Contains('\\')))
            throw new ServerRequestException(400, folder ? "invalid_folder_name" : "invalid_agent_name");
        try
        {
            var normalized = name.Normalize(NormalizationForm.FormC);
            if (normalized.Length > 128) throw new ServerRequestException(400, folder ? "invalid_folder_name" : "invalid_agent_name");
            return normalized;
        }
        catch (ArgumentException) { throw new ServerRequestException(400, "invalid_name_encoding"); }
    }
    private static async Task<IReadOnlyList<AgentFolder>> ReadFoldersAsync(NpgsqlConnection db, NpgsqlTransaction? tx, CancellationToken token)
    {
        await using var cmd = Command(db, tx, "SELECT id,parent_id,name FROM server_agent_folders ORDER BY id LIMIT 1001");
        await using var reader = await cmd.ExecuteReaderAsync(token);
        var folders = new List<AgentFolder>();
        while (await reader.ReadAsync(token)) folders.Add(new(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetString(2)));
        if (folders.Count > MaximumFolders) throw new ServerRequestException(409, "folder_limit");
        return folders;
    }
    public static IReadOnlyList<AgentFolderOption> FolderOptions(IReadOnlyList<AgentFolder> folders)
    {
        if (folders.Count > MaximumFolders) throw new ServerRequestException(409, "folder_limit");
        var result = new List<AgentFolderOption>();
        var visited = new HashSet<Guid>();
        void Children(Guid? parent, string prefix, int depth)
        {
            if (depth > MaximumFolderDepth) throw new ServerRequestException(409, "folder_depth_limit");
            foreach (var folder in folders.Where(f => f.ParentId == parent).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Id))
            {
                if (!visited.Add(folder.Id)) throw new ServerRequestException(409, "invalid_folder_tree");
                var path = prefix.Length == 0 ? folder.Name : prefix + " / " + folder.Name;
                result.Add(new(folder.Id, folder.ParentId, folder.Name, path, depth));
                // An empty level past the maximum is allowed; no folder may exceed the maximum.
                if (folders.Any(f => f.ParentId == folder.Id)) Children(folder.Id, path, depth + 1);
            }
        }
        Children(null, "", 1);
        if (visited.Count != folders.Count) throw new ServerRequestException(409, "invalid_folder_tree");
        return result;
    }
    public async Task<IReadOnlyList<AgentFolderOption>> FoldersAsync(CancellationToken token = default)
    {
        await using var db = await source.OpenConnectionAsync(token);
        return FolderOptions(await ReadFoldersAsync(db, null, token));
    }
    private static async Task<Guid?> ResolveFolderAsync(NpgsqlConnection db, NpgsqlTransaction tx, Guid userId,
        Guid? parent, string? newName, CancellationToken token)
    {
        var folders = await ReadFoldersAsync(db, tx, token);
        if (parent == Guid.Empty || parent is { } id && !folders.Any(f => f.Id == id)) throw new ServerRequestException(404, "folder_not_found");
        if (newName is null) return parent;
        return await CreateFolderCoreAsync(db, tx, userId, newName, parent, folders, token);
    }
    private static async Task<Guid> CreateFolderCoreAsync(NpgsqlConnection db, NpgsqlTransaction tx, Guid userId,
        string name, Guid? parent, IReadOnlyList<AgentFolder> folders, CancellationToken token)
    {
        name = OrganizationName(name, folder: true);
        if (folders.Count >= MaximumFolders) throw new ServerRequestException(409, "folder_limit");
        var folder = new AgentFolder(Guid.NewGuid(), parent, name);
        _ = FolderOptions(folders.Append(folder).ToArray());
        await using var cmd = Command(db, tx, "INSERT INTO server_agent_folders(id,parent_id,name,name_key) VALUES(@id,@parent,@name,@key)",
            ("id", folder.Id), ("parent", parent), ("name", name), ("key", name.ToUpperInvariant()));
        try { await cmd.ExecuteNonQueryAsync(token); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        { throw new ServerRequestException(409, "folder_name_exists"); }
        await AuditAsync(db, tx, userId, null, "FolderCreated", token);
        return folder.Id;
    }
    public async Task<Guid> CreateFolderAsync(Guid userId, FolderRequest request, CancellationToken token = default)
    {
        var name = OrganizationName(request.Name, folder: true);
        await using var db = await source.OpenConnectionAsync(token);
        await using var tx = await db.BeginTransactionAsync(token);
        await OrganizationLockAsync(db, tx, token);
        var id = await ResolveFolderAsync(db, tx, userId, request.ParentId, name, token);
        await tx.CommitAsync(token);
        return id!.Value;
    }
    public async Task UpdateFolderAsync(Guid userId, Guid id, FolderRequest request, CancellationToken token = default)
    {
        var name = OrganizationName(request.Name, folder: true);
        await using var db = await source.OpenConnectionAsync(token);
        await using var tx = await db.BeginTransactionAsync(token);
        await OrganizationLockAsync(db, tx, token);
        var folders = await ReadFoldersAsync(db, tx, token);
        if (!folders.Any(f => f.Id == id)) throw new ServerRequestException(404, "folder_not_found");
        if (request.ParentId == Guid.Empty || request.ParentId is { } parent && !folders.Any(f => f.Id == parent)) throw new ServerRequestException(404, "folder_not_found");
        if (request.ParentId == id) throw new ServerRequestException(400, "folder_cycle");
        var changed = folders.Select(f => f.Id == id ? f with { Name = name, ParentId = request.ParentId } : f).ToArray();
        try { _ = FolderOptions(changed); }
        catch (ServerRequestException ex) when (ex.Code == "invalid_folder_tree") { throw new ServerRequestException(400, "folder_cycle"); }
        await using var cmd = Command(db, tx, "UPDATE server_agent_folders SET name=@name,name_key=@key,parent_id=@parent WHERE id=@id",
            ("name", name), ("key", name.ToUpperInvariant()), ("parent", request.ParentId), ("id", id));
        try { await cmd.ExecuteNonQueryAsync(token); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { throw new ServerRequestException(409, "folder_name_exists"); }
        await AuditAsync(db, tx, userId, null, "FolderChanged", token);
        await tx.CommitAsync(token);
    }
    public async Task DeleteFolderAsync(Guid userId, Guid id, CancellationToken token = default)
    {
        await using var db = await source.OpenConnectionAsync(token);
        await using var tx = await db.BeginTransactionAsync(token);
        await OrganizationLockAsync(db, tx, token);
        await using (var check = Command(db, tx, """
            SELECT EXISTS(SELECT 1 FROM server_agent_folders WHERE id=@id),
              EXISTS(SELECT 1 FROM server_agent_folders WHERE parent_id=@id)
              OR EXISTS(SELECT 1 FROM server_agents WHERE folder_id=@id)
              OR EXISTS(SELECT 1 FROM server_invitations WHERE folder_id=@id AND consumed_utc IS NULL AND expires_utc>now())
            """, ("id", id)))
        {
            await using var reader = await check.ExecuteReaderAsync(token); await reader.ReadAsync(token);
            if (!reader.GetBoolean(0)) throw new ServerRequestException(404, "folder_not_found");
            if (reader.GetBoolean(1)) throw new ServerRequestException(409, "folder_not_empty");
        }
        await using (var clear = Command(db, tx, "UPDATE server_invitations SET folder_id=NULL WHERE folder_id=@id", ("id", id))) await clear.ExecuteNonQueryAsync(token);
        await using (var remove = Command(db, tx, "DELETE FROM server_agent_folders WHERE id=@id", ("id", id))) await remove.ExecuteNonQueryAsync(token);
        await AuditAsync(db, tx, userId, null, "FolderDeleted", token);
        await tx.CommitAsync(token);
    }
    internal async Task MoveAgentAsync(Guid userId, Guid agentId, Guid? previous, Guid? destination, CancellationToken token)
    {
        await using var db = await source.OpenConnectionAsync(token);
        await using var tx = await db.BeginTransactionAsync(token);
        await OrganizationLockAsync(db, tx, token);
        var folder = await ResolveFolderAsync(db, tx, userId, destination, null, token);
        await using var update = Command(db, tx, """
            UPDATE server_agents SET folder_id=@folder
            WHERE id=@id AND folder_id IS NOT DISTINCT FROM @previous::uuid
            """, ("folder", folder), ("id", agentId), ("previous", previous));
        if (await update.ExecuteNonQueryAsync(token) != 1)
            throw new ServerRequestException(409, "agent_folder_changed");
        await AuditAsync(db, tx, userId, agentId, "AgentIdentityChanged", token);
        await tx.CommitAsync(token);
    }

    public async Task UpdateAgentIdentityAsync(Guid userId, Guid id, AgentIdentityRequest request, CancellationToken token = default)
    {
        var name = OrganizationName(request.Name, folder: false);
        await using var db = await source.OpenConnectionAsync(token);
        await using var tx = await db.BeginTransactionAsync(token);
        await OrganizationLockAsync(db, tx, token);
        var folder = await ResolveFolderAsync(db, tx, userId, request.FolderId, request.NewFolderName, token);
        await using var update = Command(db, tx, "UPDATE server_agents SET name=@name,folder_id=@folder WHERE id=@id", ("name", name), ("folder", folder), ("id", id));
        if (await update.ExecuteNonQueryAsync(token) != 1) throw new ServerRequestException(404, "agent_not_found");
        await AuditAsync(db, tx, userId, id, "AgentIdentityChanged", token);
        await tx.CommitAsync(token);
    }
}
