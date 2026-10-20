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
    public string DisplayName { get; init; } = "1C-Backup Server";
    public string? PublicAddress { get; init; }
    public bool HasValidIdentity() => !string.IsNullOrWhiteSpace(DisplayName) && DisplayName.Length <= 128
        && !DisplayName.Any(char.IsControl) && (PublicAddress is null || ValidPublicAddress(PublicAddress));
    public static bool ValidPublicAddress(string value) => !value.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch) || ch == '\\')
        && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && uri.HostNameType is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6
        && uri.Host is not "0.0.0.0" and not "[::]" && uri.Port is >= 1 and <= 65535
        && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
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
