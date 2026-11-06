#if SERVER_CONTRACTS
namespace SPBackup.Server;
#else
namespace SPBackup.Core;
#endif

/// <summary>Compatibility claims, not authentication or proof that a backup was executed.</summary>
public static class AgentCapabilities
{
    public const string Header = "SP-Backup-Capabilities";
    public const string ConfigurationV1 = "configuration-v1";
    public const string StrictConfigurationV1 = "strict-configuration-v1";
    public const string PostgreSqlTlsV1 = "postgresql-tls-verify-full-v1";
    public const string ManagedExecutionV1 = "managed-execution-v1";
    public const string ConfigurationReceiptsV1 = "configuration-receipts-v1";

    // Standalone and library clients do not claim managed execution. The initialized
    // Managed host explicitly opts in for its bound API and monitoring connection.
    public static IReadOnlyList<string> Reported { get; } = Array.AsReadOnly(new[]
        { ConfigurationV1, StrictConfigurationV1, PostgreSqlTlsV1, ConfigurationReceiptsV1 });
    public static string HeaderValue => string.Join(",", Reported);
    internal static string ExecutionHeaderValue(bool managed) => managed ? HeaderValue + "," + ManagedExecutionV1 : HeaderValue;

    private static bool First(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9';
    public static bool Valid(IReadOnlyList<string>? values) => values is { Count: <= 32 }
        && values.All(v => v is { Length: > 0 and <= 64 } && First(v[0]) && v.All(c => First(c) || c is '-' or '.'))
        && values.Distinct(StringComparer.Ordinal).Count() == values.Count;

    public static IReadOnlyList<string> Parse(string? header)
    {
        if (header is null || header.Length == 0) return [];
        if (header.Length > 2048) throw new ArgumentException("Некорректный список возможностей агента.");
        var values = header.Split(',').Select(v => v.Trim()).ToArray();
        if (values.Length > 32 || !Valid(values.Distinct(StringComparer.Ordinal).ToArray()))
            throw new ArgumentException("Некорректный список возможностей агента.");
        return values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }
}
