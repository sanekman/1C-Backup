using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net.Http;
using System.Security.Authentication;
using SPBackup.Core.Monitoring;

namespace SPBackup.Core;

/// <summary>Collects bounded, typed reasons without copying exception text or local paths to the server.</summary>
internal sealed class BackupRunIssueContext : IDisposable
{
    private static readonly AsyncLocal<BackupRunIssueContext?> current = new();
    private readonly BackupRunIssueContext? previous;
    private readonly ConcurrentDictionary<MonitoredIssue, byte> issues = new();
    private BackupRunIssueContext() { previous = current.Value; current.Value = this; }
    internal static BackupRunIssueContext Begin() => new();
    internal IReadOnlyList<MonitoredIssue> Snapshot() => issues.Keys.OrderBy(i => i.DestinationIndex)
        .ThenBy(i => i.Stage, StringComparer.Ordinal).ThenBy(i => i.Code, StringComparer.Ordinal)
        .ThenBy(i => i.Severity, StringComparer.Ordinal).ThenBy(i => i.NativeCode).Take(256).ToArray();
    internal static void Report(string code, string stage, string severity = "Warning", int? destinationIndex = null)
        => Add(new(code, severity, stage, destinationIndex));
    private static void Add(MonitoredIssue issue)
    {
        if (current.Value is { } scope && scope.issues.Count < 256 && MonitoredIssueCodes.Valid(issue))
            scope.issues.TryAdd(issue, 0);
    }
    internal static void Report(Exception error, string stage, string severity = "Warning", int? destinationIndex = null)
    {
        string? code = null;
        int? native = null;
        // Wrappers often preserve the original Win32 error only in an inner exception.
        for (var inner = error; inner is not null; inner = inner.InnerException)
        {
            if (inner.Data[typeof(BackupDiskSpace)] is true) { code = "DiskFull"; break; }
            var number = FileStorageFailures.Code(inner);
            var recognized = number switch
            {
                5 => "AccessDenied", 86 or 1326 or 1327 or 1330 or 1385 or 1909 => "NetworkLogonFailed",
                1219 => "NetworkCredentialConflict", 39 or 112 => "DiskFull", 2 or 3 => "FileNotFound",
                32 or 33 => "FileBusy", 53 or 54 or 58 or 59 or 64 or 67 or 121 or 1222 or 1231 or 1232 or 1236 or 1237 => "NetworkUnavailable",
                _ => null
            };
            if (recognized is not null) { code = recognized; native = number; break; }
            if (inner is AuthenticationException) { code = "TlsFailed"; break; }
            if (inner is HttpRequestException { StatusCode: { } status })
            { code = "HttpFailed"; native = (int)status; break; }
            // Prefer deeper TLS/native causes to a general outer IOException/HttpRequestException.
        }
        code ??= error switch
        {
            UnauthorizedAccessException => "AccessDenied", FileNotFoundException or DirectoryNotFoundException => "FileNotFound",
            TimeoutException or OperationCanceledException => "Timeout",
            _ => stage == "Retention" ? "RetentionFailed" : "BackupFailed"
        };
        Add(new(code, severity, stage, destinationIndex, native));
    }
    public void Dispose() => current.Value = previous;
}
