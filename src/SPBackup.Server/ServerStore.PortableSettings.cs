using System.Text;
using System.Text.Json;
using Npgsql;

namespace SPBackup.Server;

public sealed record PortablePanelSettings(string DisplayName, string TimeZoneId);
public sealed record PortableAgentPlan(Guid AgentId, string AgentName, Guid? FolderId, IReadOnlyList<ManagedFileJob> Jobs);
public sealed record ImportedAgent(Guid AgentId, string AgentName, Guid? FolderId, int JobCount);
public sealed record PortableServerSettings(int FormatVersion, PortablePanelSettings Panel, IReadOnlyList<AgentFolder> Folders,
    IReadOnlyList<PortableAgentPlan> Plans);

public sealed partial class ServerStore
{
    internal async Task<PortablePanelSettings?> ImportedPanelAsync(CancellationToken token)
    {
        await using var db = await source.OpenConnectionAsync(token);
        await using var cmd = Command(db, null, "SELECT display_name,time_zone_id FROM server_portable_panel_settings");
        await using var reader = await cmd.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? new(reader.GetString(0), reader.GetString(1)) : null;
    }

    internal async Task<bool> SaveImportedZoneAsync(string zone, CancellationToken token)
    {
        await using var db = await source.OpenConnectionAsync(token);
        await using var cmd = Command(db, null, "UPDATE server_portable_panel_settings SET time_zone_id=@zone", ("zone", zone));
        return await cmd.ExecuteNonQueryAsync(token) == 1;
    }

    public async Task<IReadOnlyList<ImportedAgent>> ImportedAgentsAsync(CancellationToken token)
    {
        await using var db = await source.OpenConnectionAsync(token);
        await using var cmd = Command(db, null, """
            SELECT agent_id,agent_name,folder_id,jsonb_array_length(jobs) FROM server_imported_plans ORDER BY agent_name,agent_id
            """);
        await using var reader = await cmd.ExecuteReaderAsync(token);
        var rows = new List<ImportedAgent>();
        while (await reader.ReadAsync(token))
            rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetInt32(3)));
        return rows;
    }

    private static async Task<PortableAgentPlan?> ReadImportedPlanAsync(NpgsqlConnection db, NpgsqlTransaction tx, Guid agent, CancellationToken token)
    {
        await using var cmd = Command(db, tx, "SELECT agent_name,folder_id,jobs::text FROM server_imported_plans WHERE agent_id=@id", ("id", agent));
        await using var reader = await cmd.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        return new(agent, reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), DeserializeJobs(reader.GetString(2)));
    }

    internal async Task<PortableServerSettings> ExportPortableAsync(PortablePanelSettings panel, CancellationToken token)
    {
        await using var db = await source.OpenConnectionAsync(token);
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, token);
        var folders = await ReadFoldersAsync(db, tx, token);
        await using var cmd = Command(db, tx, """
            SELECT a.id,a.name,a.folder_id,c.revision,c.document::text
            FROM server_agents a JOIN server_configurations c ON c.agent_id=a.id WHERE NOT a.revoked
            UNION ALL
            SELECT p.agent_id,p.agent_name,p.folder_id,NULL,p.jobs::text FROM server_imported_plans p
            ORDER BY 2,1 LIMIT 1001
            """);
        await using var reader = await cmd.ExecuteReaderAsync(token);
        var plans = new List<PortableAgentPlan>();
        var planBytes = 0;
        while (await reader.ReadAsync(token))
        {
            var agent = reader.GetGuid(0);
            var json = reader.GetString(4);
            planBytes += Encoding.UTF8.GetByteCount(json);
            if (planBytes > ServerSettingsTransfer.MaximumBytes)
                throw new ServerRequestException(413, "portable_settings_too_large");
            var jobs = reader.IsDBNull(3) ? DeserializeJobs(json) : Editable(agent, reader.GetInt64(3), json).Jobs;
            plans.Add(new(agent, reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), jobs));
        }
        if (plans.Count > 1000) throw new ServerRequestException(413, "portable_settings_too_large");
        return new(1, panel, folders, plans);
    }

    private static IReadOnlyList<ManagedFileJob> DeserializeJobs(string json) =>
        JsonSerializer.Deserialize<ManagedFileJob[]>(json, FileConfigurationContract.Json) ?? throw new JsonException();

    internal async Task ImportPortableAsync(Guid administrator, Guid stamp, PortableServerSettings portable, CancellationToken token)
    {
        if (portable is not { FormatVersion: 1, Panel: not null, Folders.Count: <= MaximumFolders, Plans.Count: <= 1000 } ||
            !new ServerOptions { DisplayName = portable.Panel.DisplayName }.HasValidIdentity())
            throw new ServerRequestException(400, "invalid_portable_settings");
        var zone = ServerTimeZone.Resolve(portable.Panel.TimeZoneId);
        var folders = portable.Folders.Select(folder =>
        {
            if (folder is null || folder.Id == Guid.Empty || folder.ParentId == Guid.Empty)
                throw new ServerRequestException(400, "invalid_portable_settings");
            return folder with { Name = OrganizationName(folder.Name, folder: true) };
        }).ToArray();
        var folderIds = folders.Select(folder => folder.Id).ToHashSet();
        if (folderIds.Count != folders.Length || folders.Select(folder => (folder.ParentId, folder.Name.ToUpperInvariant())).Distinct().Count() != folders.Length)
            throw new ServerRequestException(400, "invalid_portable_settings");
        var ordered = FolderOptions(folders).Select(folder => new AgentFolder(folder.Id, folder.ParentId, folder.Name)).ToArray();
        var planIds = new HashSet<Guid>();
        var plans = new List<(PortableAgentPlan Plan, string Jobs)>();
        foreach (var plan in portable.Plans)
        {
            if (plan is null || plan.AgentId == Guid.Empty || !planIds.Add(plan.AgentId) ||
                plan.FolderId is { } folder && !folderIds.Contains(folder))
                throw new ServerRequestException(400, "invalid_portable_settings");
            FileConfigurationContract.Validate(1, plan.Jobs);
            var json = JsonSerializer.Serialize(plan.Jobs, FileConfigurationContract.Json);
            var document = JsonSerializer.Serialize(new ManagedFileConfiguration(plan.AgentId, 1, plan.Jobs), FileConfigurationContract.Json);
            if (Encoding.UTF8.GetByteCount(document) > FileConfigurationContract.MaxBytes)
                throw new ServerRequestException(413, "configuration_too_large");
            plans.Add((plan with { AgentName = OrganizationName(plan.AgentName, folder: false) }, json));
        }

        await using var db = await source.OpenConnectionAsync(token);
        await using var tx = await db.BeginTransactionAsync(token);
        await OrganizationLockAsync(db, tx, token);
        await using (var validateUser = Command(db, tx, """
            SELECT security_stamp FROM server_users WHERE id=@id AND enabled AND role='Administrator' FOR SHARE
            """, ("id", administrator)))
            if (await validateUser.ExecuteScalarAsync(token) is not Guid currentStamp || currentStamp != stamp)
                throw new ServerRequestException(401, "invalid_session");
        await using (var target = Command(db, tx, """
            SELECT EXISTS(SELECT 1 FROM server_agents) OR EXISTS(SELECT 1 FROM server_agent_folders)
                OR EXISTS(SELECT 1 FROM server_imported_plans) OR EXISTS(SELECT 1 FROM server_portable_panel_settings)
                OR EXISTS(SELECT 1 FROM server_invitations)
            """))
            if ((bool)(await target.ExecuteScalarAsync(token))!)
                throw new ServerRequestException(409, "portable_settings_target_not_empty");
        foreach (var folder in ordered)
        {
            await using var insert = Command(db, tx, "INSERT INTO server_agent_folders(id,parent_id,name,name_key) VALUES(@id,@parent,@name,@key)",
                ("id", folder.Id), ("parent", folder.ParentId), ("name", folder.Name), ("key", folder.Name.ToUpperInvariant()));
            await insert.ExecuteNonQueryAsync(token);
        }
        foreach (var (plan, jobs) in plans)
        {
            await using var insert = Command(db, tx, "INSERT INTO server_imported_plans(agent_id,agent_name,folder_id,jobs) VALUES(@id,@name,@folder,@jobs::jsonb)",
                ("id", plan.AgentId), ("name", plan.AgentName), ("folder", plan.FolderId), ("jobs", jobs));
            await insert.ExecuteNonQueryAsync(token);
        }
        await using (var insert = Command(db, tx, "INSERT INTO server_portable_panel_settings(display_name,time_zone_id) VALUES(@name,@zone)",
            ("name", portable.Panel.DisplayName), ("zone", zone.Id))) await insert.ExecuteNonQueryAsync(token);
        await AuditAsync(db, tx, administrator, null, "PortableSettingsImported", token);
        await tx.CommitAsync(token);
    }
}
