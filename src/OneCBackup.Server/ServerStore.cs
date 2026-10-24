using System.Data;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using NpgsqlTypes;

namespace OneCBackup.Server;

/// <summary>Parameterized PostgreSQL operations. Invitation consumption, registration and audit commit together.</summary>
public sealed partial class ServerStore(NpgsqlDataSource source)
{
    public const int SchemaVersion = 2;
    private readonly PasswordHasher<ServerUser> passwords = new();
    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql,
        params (string Name, object? Value)[] values)
    {
        var cmd = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
    public async Task MigrateAsync(CancellationToken token = default)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using (var gate = Command(connection, transaction, "SELECT pg_advisory_xact_lock(1731062101); CREATE TABLE IF NOT EXISTS server_schema (version integer PRIMARY KEY, applied_utc timestamptz NOT NULL DEFAULT now());"))
            await gate.ExecuteNonQueryAsync(token);
        int version;
        await using (var check = Command(connection, transaction, "SELECT COALESCE(max(version),0) FROM server_schema"))
            version = (int)(await check.ExecuteScalarAsync(token))!;
        if (version > SchemaVersion) throw new ServerRequestException(409, "schema_newer_than_application");
        if (version == 0)
        {
            using var script = typeof(ServerStore).Assembly.GetManifestResourceStream("OneCBackup.Server.Migrations.001-foundation.sql")!;
            using var reader = new StreamReader(script); var sql = await reader.ReadToEndAsync(token);
            await using var migration = Command(connection, transaction, sql + "\nINSERT INTO server_schema(version) VALUES(1);");
            await migration.ExecuteNonQueryAsync(token);
        }
        if (version < 2)
        {
            using var script = typeof(ServerStore).Assembly.GetManifestResourceStream("OneCBackup.Server.Migrations.002-monitoring.sql")!;
            using var reader = new StreamReader(script);
            await using var migration = Command(connection, transaction, await reader.ReadToEndAsync(token));
            await migration.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
    }
    public async Task<bool> ReadyAsync(CancellationToken token = default)
    {
        try
        {
            await using var connection = await source.OpenConnectionAsync(token);
            await using var cmd = Command(connection, null, "SELECT (SELECT max(version) FROM server_schema)=@version AND EXISTS(SELECT 1 FROM server_users)", ("version", SchemaVersion));
            return await cmd.ExecuteScalarAsync(token) is true;
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException) { return false; }
    }
    public async Task BootstrapAsync(string name, string password, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl)
            || password.Length is < 12 or > 256 || password.Any(char.IsControl)) throw new ServerRequestException(400, "invalid_bootstrap_credentials");
        var user = new ServerUser(Guid.NewGuid(), name.Trim().ToLowerInvariant(), "", Guid.NewGuid());
        var hash = passwords.HashPassword(user, password);
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using (var gate = Command(connection, transaction, "SELECT pg_advisory_xact_lock(1731062102)")) await gate.ExecuteNonQueryAsync(token);
        await using (var exists = Command(connection, transaction, "SELECT EXISTS(SELECT 1 FROM server_users)"))
            if ((bool)(await exists.ExecuteScalarAsync(token))!) throw new ServerRequestException(409, "administrator_already_exists");
        await using (var insert = Command(connection, transaction, "INSERT INTO server_users(id,name,password_hash,security_stamp) VALUES(@id,@name,@hash,@stamp)",
            ("id", user.Id), ("name", user.Name), ("hash", hash), ("stamp", user.SecurityStamp))) await insert.ExecuteNonQueryAsync(token);
        await AuditAsync(connection, transaction, user.Id, null, "AdministratorCreated", token);
        await transaction.CommitAsync(token);
    }
    public async Task<ServerUser?> FindUserAsync(string name, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var cmd = Command(connection, null, "SELECT id,name,password_hash,security_stamp FROM server_users WHERE name=@name", ("name", name.Trim().ToLowerInvariant()));
        await using var reader = await cmd.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetGuid(3)) : null;
    }
    private readonly ServerUser dummy = new(Guid.Empty, "", new PasswordHasher<ServerUser>().HashPassword(null!, ServerTokens.Create()), Guid.Empty);
    public async Task<ServerUser?> LoginAsync(string name, string password, CancellationToken token)
    {
        var user = await FindUserAsync(name, token); var candidate = user ?? dummy;
        return passwords.VerifyHashedPassword(candidate, candidate.PasswordHash, password) != PasswordVerificationResult.Failed ? user : null;
    }
    public async Task<bool> ValidateSessionAsync(Guid id, Guid stamp, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var cmd = Command(connection, null, "SELECT EXISTS(SELECT 1 FROM server_users WHERE id=@id AND security_stamp=@stamp)", ("id", id), ("stamp", stamp));
        return (bool)(await cmd.ExecuteScalarAsync(token))!;
    }
    public async Task<Invitation> InviteAsync(Guid userId, InviteRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128 || request.Name.Any(char.IsControl)
            || request.Minutes is < 1 or > 60 || request.AgentId == Guid.Empty) throw new ServerRequestException(400, "invalid_invitation");
        var id = Guid.NewGuid(); var code = ServerTokens.Create();
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        DateTimeOffset expiry;
        await using (var cmd = Command(connection, transaction,
            "INSERT INTO server_invitations(id,name,agent_id,token_hash,expires_utc,created_by) VALUES(@id,@name,@agent,@hash,now()+make_interval(mins=>@minutes),@user) RETURNING expires_utc",
            ("id", id), ("name", request.Name.Trim()), ("agent", request.AgentId), ("hash", ServerTokens.Hash(code)), ("minutes", request.Minutes), ("user", userId)))
            expiry = new((DateTime)(await cmd.ExecuteScalarAsync(token))!);
        await AuditAsync(connection, transaction, userId, request.AgentId, "InvitationCreated", token);
        await transaction.CommitAsync(token);
        return new(id, code, expiry);
    }
    public async Task<EnrollmentResponse> EnrollAsync(EnrollmentRequest request, CancellationToken token)
    {
        if (!ServerTokens.IsValid(request.InvitationCode) || request.AgentId == Guid.Empty || string.IsNullOrWhiteSpace(request.Name)
            || request.Name.Length > 128 || request.Name.Any(char.IsControl)) throw new ServerRequestException(400, "invalid_enrollment");
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        Guid invitationId; bool replaceRevoked;
        await using (var cmd = Command(connection, transaction,
            "SELECT id,agent_id FROM server_invitations WHERE token_hash=@hash AND expires_utc>now() AND consumed_utc IS NULL FOR UPDATE", ("hash", ServerTokens.Hash(request.InvitationCode))))
        {
            await using var reader = await cmd.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) throw new ServerRequestException(403, "invitation_unavailable");
            invitationId = reader.GetGuid(0); replaceRevoked = !reader.IsDBNull(1) && reader.GetGuid(1) == request.AgentId;
            if (!reader.IsDBNull(1) && reader.GetGuid(1) != request.AgentId) throw new ServerRequestException(403, "invitation_agent_mismatch");
        }
        var bearer = ServerTokens.Create();
        try
        {
            await using var insert = Command(connection, transaction, "INSERT INTO server_agents(id,name,token_hash) VALUES(@id,@name,@hash) ON CONFLICT(id) DO UPDATE SET token_hash=excluded.token_hash,revoked=false,name=excluded.name WHERE server_agents.revoked AND @replace",
                ("id", request.AgentId), ("name", request.Name.Trim()), ("hash", ServerTokens.Hash(bearer)), ("replace",replaceRevoked));
            if (await insert.ExecuteNonQueryAsync(token)!=1) throw new ServerRequestException(409,"agent_already_registered");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { throw new ServerRequestException(409, "agent_already_registered"); }
        var document = JsonSerializer.Serialize(new { agentId = request.AgentId, revision = 1, jobs = Array.Empty<object>(), protocolVersion = 1 });
        await using (var config = Command(connection, transaction, "INSERT INTO server_configurations(agent_id,revision,document) VALUES(@id,1,@doc::jsonb) ON CONFLICT(agent_id) DO NOTHING", ("id", request.AgentId), ("doc", document))) await config.ExecuteNonQueryAsync(token);
        await using (var consume = Command(connection, transaction, "UPDATE server_invitations SET consumed_utc=now() WHERE id=@id", ("id", invitationId))) await consume.ExecuteNonQueryAsync(token);
        await AuditAsync(connection, transaction, null, request.AgentId, "AgentEnrolled", token);
        await transaction.CommitAsync(token);
        return new(request.AgentId, bearer);
    }
    public async Task<Guid?> AuthenticateAgentAsync(string bearer, CancellationToken token)
    {
        if (!ServerTokens.IsValid(bearer)) return null;
        await using var connection = await source.OpenConnectionAsync(token);
        await using var cmd = Command(connection, null, "SELECT id FROM server_agents WHERE token_hash=@hash AND NOT revoked", ("hash", ServerTokens.Hash(bearer)));
        return await cmd.ExecuteScalarAsync(token) is Guid id ? id : null;
    }
    public async Task<(long Revision, string Document)> ConfigurationAsync(Guid agentId, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var cmd = Command(connection, null, "SELECT revision,document::text FROM server_configurations WHERE agent_id=@id", ("id", agentId));
        await using var reader = await cmd.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new ServerRequestException(404, "agent_not_found");
        return (reader.GetInt64(0), reader.GetString(1));
    }
    public async Task HeartbeatAsync(Guid agentId, HeartbeatRequest request, CancellationToken token)
    {
        if (request.AgentId != agentId) throw new ServerRequestException(403, "agent_mismatch");
        if (string.IsNullOrWhiteSpace(request.AgentVersion) || request.AgentVersion.Length > 128 || request.AgentVersion.Any(char.IsControl)
            || request.ConfigurationRevision is < 1 || request.SentUtc == default || request.SentUtc.Offset != TimeSpan.Zero)
            throw new ServerRequestException(400, "invalid_heartbeat");
        await using var connection = await source.OpenConnectionAsync(token);
        await using var cmd = Command(connection, null,
            "UPDATE server_agents SET received_utc=now(), sent_utc=@sent, agent_version=@version, execution_available=@available, reported_revision=@revision WHERE id=@id AND NOT revoked AND (@revision::bigint IS NULL OR @revision<= (SELECT revision FROM server_configurations WHERE agent_id=@id))",
            ("id", agentId), ("sent", request.SentUtc), ("version", request.AgentVersion), ("available", request.BackupExecutionAvailable), ("revision", request.ConfigurationRevision));
        if (await cmd.ExecuteNonQueryAsync(token) != 1) throw new ServerRequestException(409, "heartbeat_revision_unavailable");
    }
    public async Task<AgentPage> AgentsAsync(int page, CancellationToken token)
    {
        if (page is < 0 or > 10000) throw new ServerRequestException(400, "invalid_page");
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        int total;
        await using (var count = Command(connection, transaction, "SELECT count(*)::integer FROM server_agents")) total = (int)(await count.ExecuteScalarAsync(token))!;
        await using var cmd = Command(connection, transaction, "SELECT a.id,a.name,a.registered_utc,a.revoked,a.received_utc,a.sent_utc,a.agent_version,a.execution_available,a.reported_revision,c.revision FROM server_agents a JOIN server_configurations c ON c.agent_id=a.id ORDER BY a.registered_utc DESC,a.id LIMIT 50 OFFSET @offset", ("offset", page * 50));
        var agents = new List<AgentRecord>();
        await using (var reader = await cmd.ExecuteReaderAsync(token)) while (await reader.ReadAsync(token)) agents.Add(ReadAgent(reader));
        await transaction.CommitAsync(token);
        return new(agents, total, page, 50);
    }
    public async Task<AgentRecord?> AgentAsync(Guid id, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var cmd = Command(connection, null, "SELECT a.id,a.name,a.registered_utc,a.revoked,a.received_utc,a.sent_utc,a.agent_version,a.execution_available,a.reported_revision,c.revision FROM server_agents a JOIN server_configurations c ON c.agent_id=a.id WHERE a.id=@id", ("id", id));
        await using var reader = await cmd.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadAgent(reader) : null;
    }
    private static AgentRecord ReadAgent(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetBoolean(3),
        reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4), reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
        reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetBoolean(7), reader.IsDBNull(8) ? null : reader.GetInt64(8), reader.GetInt64(9));
    public async Task RevokeAsync(Guid userId, Guid agentId, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using (var cmd = Command(connection, transaction, "UPDATE server_agents SET token_hash=NULL,revoked=true WHERE id=@id", ("id", agentId)))
            if (await cmd.ExecuteNonQueryAsync(token) != 1) throw new ServerRequestException(404, "agent_not_found");
        await AuditAsync(connection, transaction, userId, agentId, "AgentRevoked", token);
        await transaction.CommitAsync(token);
    }
    private static async Task AuditAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid? user, Guid? agent, string kind, CancellationToken token)
    {
        await using var cmd = Command(connection, transaction, "INSERT INTO server_audit(user_id,agent_id,event) VALUES(@user,@agent,@event)", ("user", user), ("agent", agent), ("event", kind));
        await cmd.ExecuteNonQueryAsync(token);
    }
}
