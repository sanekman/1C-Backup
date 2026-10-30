using System.Security.Cryptography;
using System.Text;

namespace SPBackup.Server;

public sealed record ConfigurationDecision(long Revision, Guid OfferId, string Status, string? Reason,
    DateTimeOffset? ReceivedUtc, DateTimeOffset? ObservedUtc);
public sealed record ConfigurationDecisionState(Guid AgentId, long DesiredRevision, bool Revoked,
    ConfigurationDecision? Current, ConfigurationDecision? LastAccepted);

public sealed partial class ServerStore
{
    public async Task<Guid> RegisterConfigurationOfferAsync(Guid agent, long revision, string document, byte[] authenticatedGeneration, CancellationToken token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(document));
        await using var db = await source.OpenConnectionAsync(token);
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, token);
        byte[] generation;
        await using (var row = Command(db, tx, "SELECT token_hash FROM server_agents WHERE id=@id AND NOT revoked FOR SHARE", ("id", agent)))
            generation = await row.ExecuteScalarAsync(token) as byte[] ?? throw new ServerRequestException(403, "agent_revoked");
        if (!CryptographicOperations.FixedTimeEquals(generation, authenticatedGeneration)) throw new ServerRequestException(403, "agent_connection_changed");
        await using (var row = Command(db, tx, "SELECT revision,document::text FROM server_configurations WHERE agent_id=@id FOR SHARE", ("id", agent)))
        await using (var reader = await row.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token) || reader.GetInt64(0) != revision || reader.GetString(1) != document)
                throw new ServerRequestException(409, "configuration_changed_retry");
        }
        await using (var insert = Command(db, tx, """
            INSERT INTO server_configuration_offers(offer_id,agent_id,generation,revision,document_hash)
            VALUES(@offer,@id,@generation,@revision,@hash) ON CONFLICT(agent_id,generation,revision) DO NOTHING
            """, ("offer", Guid.NewGuid()), ("id", agent), ("generation", generation), ("revision", revision), ("hash", hash)))
            await insert.ExecuteNonQueryAsync(token);
        Guid result;
        await using (var row = Command(db, tx, "SELECT offer_id,document_hash FROM server_configuration_offers WHERE agent_id=@id AND generation=@generation AND revision=@revision",
            ("id", agent), ("generation", generation), ("revision", revision)))
        await using (var reader = await row.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token) || !CryptographicOperations.FixedTimeEquals(hash, reader.GetFieldValue<byte[]>(1)))
                throw new ServerRequestException(409, "configuration_changed_without_revision");
            result = reader.GetGuid(0);
        }
        await tx.CommitAsync(token);
        return result;
    }

    public async Task SaveConfigurationReceiptAsync(Guid agent, ConfigurationReceipt receipt, byte[] authenticatedGeneration, CancellationToken token)
    {
        if (receipt.AgentId != agent) throw new ServerRequestException(403, "agent_mismatch");
        if (!receipt.Valid()) throw new ServerRequestException(400, "invalid_configuration_receipt");
        await using var db = await source.OpenConnectionAsync(token);
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, token);
        byte[] generation;
        await using (var row = Command(db, tx, "SELECT token_hash FROM server_agents WHERE id=@id AND NOT revoked FOR SHARE", ("id", agent)))
            generation = await row.ExecuteScalarAsync(token) as byte[] ?? throw new ServerRequestException(403, "agent_revoked");
        if (!CryptographicOperations.FixedTimeEquals(generation, authenticatedGeneration)) throw new ServerRequestException(403, "agent_connection_changed");
        await using (var row = Command(db, tx, "SELECT revision,generation,status,reason FROM server_configuration_offers WHERE offer_id=@offer AND agent_id=@id FOR UPDATE",
            ("offer", receipt.OfferId), ("id", agent)))
        await using (var reader = await row.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token)) throw new ServerRequestException(404, "configuration_offer_not_found");
            if (!CryptographicOperations.FixedTimeEquals(generation, reader.GetFieldValue<byte[]>(1)))
                throw new ServerRequestException(410, "configuration_offer_expired");
            if (reader.GetInt64(0) != receipt.Revision) throw new ServerRequestException(409, "configuration_offer_revision_mismatch");
            if (!reader.IsDBNull(2))
            {
                var status = reader.GetString(2);
                if (status == "Accepted") return; // Acceptance is absorbing; delayed rejection cannot undo it.
                // First rejection is retained until acceptance; retries do not change timestamps or audit.
                if (status == receipt.Status) return;
            }
        }
        await using (var update = Command(db, tx, "UPDATE server_configuration_offers SET status=@status,reason=@reason,observed_utc=@observed,received_utc=now() WHERE offer_id=@offer",
            ("status", receipt.Status), ("reason", receipt.Reason), ("observed", receipt.ObservedUtc), ("offer", receipt.OfferId)))
            await update.ExecuteNonQueryAsync(token);
        await AuditAsync(db, tx, null, agent, receipt.Status == "Accepted" ? "ConfigurationAccepted" : "ConfigurationRejected", token);
        await tx.CommitAsync(token);
    }

    public async Task<ConfigurationDecisionState> ConfigurationDecisionsAsync(Guid agent, CancellationToken token)
    {
        await using var db = await source.OpenConnectionAsync(token);
        await using var command = Command(db, null, """
            SELECT c.revision,a.revoked,
                offered.revision,offered.offer_id,offered.status,offered.reason,offered.received_utc,offered.observed_utc,
                accepted.revision,accepted.offer_id,accepted.status,accepted.reason,accepted.received_utc,accepted.observed_utc
            FROM server_agents a JOIN server_configurations c ON c.agent_id=a.id
            LEFT JOIN server_configuration_offers offered ON offered.agent_id=a.id AND offered.generation=a.token_hash AND offered.revision=c.revision
                AND offered.document_hash=sha256(convert_to(c.document::text,'UTF8'))
            LEFT JOIN LATERAL (SELECT * FROM server_configuration_offers o WHERE o.agent_id=a.id AND o.generation=a.token_hash AND o.status='Accepted' ORDER BY o.revision DESC LIMIT 1) accepted ON true
            WHERE a.id=@id
            """, ("id", agent));
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new ServerRequestException(404, "agent_not_found");
        ConfigurationDecision? Read(int offset) => reader.IsDBNull(offset) ? null : new(reader.GetInt64(offset), reader.GetGuid(offset + 1),
            reader.IsDBNull(offset + 2) ? "Awaiting" : reader.GetString(offset + 2), reader.IsDBNull(offset + 3) ? null : reader.GetString(offset + 3),
            reader.IsDBNull(offset + 4) ? null : reader.GetFieldValue<DateTimeOffset>(offset + 4),
            reader.IsDBNull(offset + 5) ? null : reader.GetFieldValue<DateTimeOffset>(offset + 5));
        return new(agent, reader.GetInt64(0), reader.GetBoolean(1), Read(2), Read(8));
    }
}
