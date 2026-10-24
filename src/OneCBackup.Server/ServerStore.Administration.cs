using System.Data;
using Microsoft.AspNetCore.Identity;

namespace OneCBackup.Server;

public enum PasswordChangeResult { Changed, IncorrectCurrent, InvalidNew, Mismatch, SamePassword, StaleSession }
public sealed record AuditEntry(long Id, DateTimeOffset ReceivedUtc, string? UserName, Guid? AgentId, string? AgentName, string Event);
public sealed record AuditPage(IReadOnlyList<AuditEntry> Entries, int Total, int Page, int PageSize);

public sealed partial class ServerStore
{
    public async Task<PasswordChangeResult> ChangePasswordAsync(Guid id, Guid expectedStamp, string current, string next,
        string confirmation, CancellationToken token = default)
    {
        if (next.Length is < 12 or > 256 || next.Any(char.IsControl)) return PasswordChangeResult.InvalidNew;
        if (next != confirmation) return PasswordChangeResult.Mismatch;
        if (current.Length is < 1 or > 256) return PasswordChangeResult.IncorrectCurrent;
        await using var db = await source.OpenConnectionAsync(token);
        await using var tx = await db.BeginTransactionAsync(token);
        ServerUser? user = null;
        await using (var select = Command(db, tx, "SELECT id,name,password_hash,security_stamp FROM server_users WHERE id=@id FOR UPDATE", ("id", id)))
        await using (var reader = await select.ExecuteReaderAsync(token))
            if (await reader.ReadAsync(token)) user = new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetGuid(3));
        if (user is null || user.SecurityStamp != expectedStamp) return PasswordChangeResult.StaleSession;
        if (passwords.VerifyHashedPassword(user, user.PasswordHash, current) == PasswordVerificationResult.Failed)
            return PasswordChangeResult.IncorrectCurrent;
        if (current == next) return PasswordChangeResult.SamePassword;
        await using (var update = Command(db, tx, "UPDATE server_users SET password_hash=@hash,security_stamp=@stamp WHERE id=@id",
            ("id", id), ("hash", passwords.HashPassword(user, next)), ("stamp", Guid.NewGuid())))
            await update.ExecuteNonQueryAsync(token);
        await AuditAsync(db, tx, id, null, "AdministratorPasswordChanged", token);
        await tx.CommitAsync(token);
        return PasswordChangeResult.Changed;
    }

    public async Task RecordAdministratorLoginAsync(Guid id, CancellationToken token)
    {
        await using var db = await source.OpenConnectionAsync(token);
        await AuditAsync(db, null, id, null, "AdministratorLogin", token);
    }

    public async Task<AuditPage> AuditPageAsync(int page, CancellationToken token = default)
    {
        if (page is < 0 or > 10000) throw new ServerRequestException(400, "invalid_page");
        await using var db = await source.OpenConnectionAsync(token);
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        int total;
        await using (var count = Command(db, tx, "SELECT count(*)::integer FROM server_audit")) total = (int)(await count.ExecuteScalarAsync(token))!;
        var entries = new List<AuditEntry>();
        await using (var command = Command(db, tx, """
            SELECT e.id,e.received_utc,u.name,e.agent_id,a.name,e.event
            FROM server_audit e LEFT JOIN server_users u ON u.id=e.user_id LEFT JOIN server_agents a ON a.id=e.agent_id
            ORDER BY e.id DESC LIMIT 50 OFFSET @offset
            """, ("offset", page * 50)))
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) entries.Add(new(reader.GetInt64(0), reader.GetFieldValue<DateTimeOffset>(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5)));
        await tx.CommitAsync(token);
        return new(entries, total, page, 50);
    }
}
