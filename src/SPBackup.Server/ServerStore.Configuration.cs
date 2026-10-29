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

    public async Task<ManagedFileConfiguration> PublishFileConfigurationAsync(Guid administrator, Guid stamp, Guid agent,
        long expected, PublishFileConfiguration request, CancellationToken token)
    {
        if (request.AgentId != agent) throw new ServerRequestException(403, "agent_mismatch");
        FileConfigurationContract.Validate(request.ProtocolVersion, request.Jobs);
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
        var document = new ManagedFileConfiguration(agent, expected + 1, request.Jobs);
        var json = JsonSerializer.Serialize(document, FileConfigurationContract.Json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > FileConfigurationContract.MaxBytes)
            throw new ServerRequestException(413, "configuration_too_large");
        await using (var write = Command(db, tx, "UPDATE server_configurations SET revision=@revision,document=@doc::jsonb,created_utc=now() WHERE agent_id=@id",
            ("revision", document.Revision), ("doc", json), ("id", agent))) await write.ExecuteNonQueryAsync(token);
        await AuditAsync(db, tx, administrator, agent, "ConfigurationPublished", token);
        await tx.CommitAsync(token);
        return document;
    }
}
