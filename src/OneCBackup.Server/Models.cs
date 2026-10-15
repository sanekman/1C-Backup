using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace OneCBackup.Server;

public sealed record ServerUser(Guid Id, string Name, string PasswordHash, Guid SecurityStamp);
public sealed record AgentRecord(Guid AgentId, string Name, DateTimeOffset RegisteredUtc, bool Revoked,
    DateTimeOffset? ReceivedUtc, DateTimeOffset? SentUtc, string? AgentVersion, bool BackupExecutionAvailable,
    long? ReportedRevision, long DesiredRevision);
public sealed record AgentPage(IReadOnlyList<AgentRecord> Agents, int Total, int Page, int PageSize);
public sealed record Invitation(Guid InvitationId, string Code, DateTimeOffset ExpiresUtc);
public sealed record EnrollmentRequest(string InvitationCode, Guid AgentId, string Name);
public sealed record EnrollmentResponse(Guid AgentId, string Token, int ProtocolVersion = 1);
// API DTOs deliberately do not depend on Windows executors or platform-specific job validation.
public sealed record HeartbeatRequest(Guid AgentId, string AgentVersion, bool BackupExecutionAvailable,
    long? ConfigurationRevision, DateTimeOffset SentUtc);
public sealed record InviteRequest(string Name, Guid? AgentId = null, int Minutes = 30);
public sealed record ServerProblem(int Status, string Title, string Code, string TraceId);
public sealed class ServerRequestException(int status, string code) : Exception
{ public int Status { get; } = status; public string Code { get; } = code; }

public static class ServerTokens
{
    public static string Create() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    public static bool IsValid(string? value) => value is { Length: 43 } && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');
    public static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}

public sealed class ServerOptions
{
    public string ConnectionString { get; init; } = "";
    public string? ConnectionStringFile { get; init; }
    public string? DatabasePasswordFile { get; init; }
    public string DataDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OneCBackupServer");
    public string? BootstrapUser { get; init; }
    public string? BootstrapPasswordFile { get; init; }
    public int OfflineAfterSeconds { get; init; } = 180;
    public string? PathBase { get; init; }
    public string[] TrustedProxies { get; init; } = [];
    public bool AllowInsecureLoopback { get; init; }
    public string? HttpsCertificatePath { get; init; }
    public string? HttpsCertificatePasswordFile { get; init; }
}
