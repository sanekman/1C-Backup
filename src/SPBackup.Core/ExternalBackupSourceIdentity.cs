using System.Security.Cryptography;
using System.Text.Json;

namespace SPBackup.Core;

/// <summary>Local binding to the configured source; contains no passwords or raw paths.</summary>
public static class ExternalBackupSourceIdentity
{
    public static string Fingerprint(ExternalBackupMonitor monitor)
    {
        var sql = monitor.Kind == ExternalBackupSourceKind.SqlServerHistory;
        var directory = sql ? "" : Path.TrimEndingDirectorySeparator(Path.GetFullPath(monitor.ReportDirectory));
        if (OperatingSystem.IsWindows()) directory = directory.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            monitor.Kind,
            Server = sql ? monitor.SqlServer.Trim().ToUpperInvariant() : "",
            Database = sql ? monitor.DatabaseName.Trim().ToUpperInvariant() : monitor.DatabaseName.Trim(),
            Directory = directory,
            monitor.TimeZoneId
        })));
    }

    public static bool Valid(string? value) => value is null || value.Length == 64 && value.All(Uri.IsHexDigit);
    public static bool Matches(ExternalBackupMonitor monitor, string? value) =>
        value is not null && string.Equals(value, Fingerprint(monitor), StringComparison.Ordinal);
}
