using System.Text.Json;

namespace SPBackup.Server;

public sealed partial class ServerStore
{
    public async Task<ManagedFileConfiguration> ReadFileConfigurationAsync(Guid agent, CancellationToken token)
    {
        var value = await ConfigurationAsync(agent, token);
        return Editable(agent, value.Revision, value.Document);
    }
    private static ManagedFileConfiguration Editable(Guid agent, long revision, string json)
    {
        try
        {
            var document = JsonSerializer.Deserialize<ManagedFileConfiguration>(json, FileConfigurationContract.Json) ?? throw new JsonException();
            FileConfigurationContract.Validate(document.ProtocolVersion, document.Jobs);
            if (document.AgentId != agent || document.Revision != revision) throw new JsonException();
            return document;
        }
        catch (Exception e) when (e is JsonException or ServerRequestException)
        { throw new ServerRequestException(409, "stored_configuration_not_editable"); }
    }

    public Task<ManagedFileConfiguration> PublishFileConfigurationAsync(Guid administrator, Guid stamp, Guid agent,
        long expected, PublishFileConfiguration request, CancellationToken token) =>
        ChangeFileConfigurationAsync(administrator, stamp, agent, expected, request, null, token);

    public Task<ManagedFileConfiguration> RestoreFileConfigurationAsync(Guid administrator, Guid stamp, Guid agent,
        long expected, long restoreRevision, CancellationToken token) =>
        ChangeFileConfigurationAsync(administrator, stamp, agent, expected, null, restoreRevision, token);

    private async Task<ManagedFileConfiguration> ChangeFileConfigurationAsync(Guid administrator, Guid stamp, Guid agent,
        long expected, PublishFileConfiguration? request, long? restoreRevision, CancellationToken token)
    {
        if (request is not null)
        {
            if (request.AgentId != agent) throw new ServerRequestException(403, "agent_mismatch");
            FileConfigurationContract.Validate(request.ProtocolVersion, request.Jobs);
        }
        await using var db = await source.OpenConnectionAsync(token);
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, token);
        // Serialize against password/session rotation as well as agent revocation.
        await using (var user = Command(db, tx, "SELECT security_stamp FROM server_users WHERE id=@id FOR SHARE", ("id", administrator)))
            if (await user.ExecuteScalarAsync(token) is not Guid currentStamp || currentStamp != stamp)
                throw new ServerRequestException(401, "invalid_session");
        await using (var row = Command(db, tx, "SELECT revoked FROM server_agents WHERE id=@id FOR UPDATE", ("id", agent)))
        {
            var revoked = await row.ExecuteScalarAsync(token);
            if (revoked is null) throw new ServerRequestException(404, "agent_not_found");
            if ((bool)revoked) throw new ServerRequestException(409, "agent_revoked");
        }
        await using (var row = Command(db, tx, "SELECT revision,document::text FROM server_configurations WHERE agent_id=@id FOR UPDATE", ("id", agent)))
        await using (var reader = await row.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token)) throw new ServerRequestException(404, "agent_not_found");
            var revision = reader.GetInt64(0);
            if (revision != expected) throw new ServerRequestException(412, "configuration_revision_conflict");
            _ = Editable(agent, revision, reader.GetString(1));
        }
        if (expected == long.MaxValue) throw new ServerRequestException(409, "configuration_revision_exhausted");
        if (restoreRevision is { } previous)
        {
            if (previous < 1 || previous >= expected) throw new ServerRequestException(409, "invalid_restore_revision");
            await using var row = Command(db, tx, "SELECT document::text FROM server_configuration_history WHERE agent_id=@id AND revision=@revision",
                ("id", agent), ("revision", previous));
            if (await row.ExecuteScalarAsync(token) is not string saved) throw new ServerRequestException(404, "configuration_history_not_found");
            var old = Editable(agent, previous, saved);
            request = new(agent, old.ProtocolVersion, old.Jobs);
        }
        var document = new ManagedFileConfiguration(agent, expected + 1, request!.Jobs);
        var json = JsonSerializer.Serialize(document, FileConfigurationContract.Json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > FileConfigurationContract.MaxBytes)
            throw new ServerRequestException(413, "configuration_too_large");
        await using (var old = Command(db, tx, "INSERT INTO server_configuration_history(agent_id,revision,document,saved_utc) SELECT agent_id,revision,document,created_utc FROM server_configurations WHERE agent_id=@id ON CONFLICT DO NOTHING", ("id", agent)))
            await old.ExecuteNonQueryAsync(token);
        await using (var write = Command(db, tx, "UPDATE server_configurations SET revision=@revision,document=@doc::jsonb,created_utc=now() WHERE agent_id=@id",
            ("revision", document.Revision), ("doc", json), ("id", agent))) await write.ExecuteNonQueryAsync(token);
        await using (var history = Command(db, tx, "INSERT INTO server_configuration_history(agent_id,revision,document,author_id,restored_from) VALUES(@id,@revision,@doc::jsonb,@author,@restored::bigint)",
            ("id", agent), ("revision", document.Revision), ("doc", json), ("author", administrator), ("restored", restoreRevision)))
            await history.ExecuteNonQueryAsync(token);
        await using (var prune = Command(db, tx, "DELETE FROM server_configuration_history WHERE agent_id=@id AND revision IN (SELECT revision FROM server_configuration_history WHERE agent_id=@id ORDER BY revision DESC OFFSET 100)", ("id", agent)))
            await prune.ExecuteNonQueryAsync(token);
        await AuditAsync(db, tx, administrator, agent, restoreRevision is null ? "ConfigurationPublished" : "ConfigurationRestored", token);
        await tx.CommitAsync(token);
        return document;
    }
}
