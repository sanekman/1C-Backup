using System.ComponentModel;
using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Data.SqlClient;

namespace SPBackup.Core;

/// <summary>Only stable codes leave the agent; exception text can contain credentials or paths.</summary>
public static class ExternalBackupPollErrors
{
    public static string Classify(Exception error)
    {
        var pending = new Queue<Exception>(); pending.Enqueue(error);
        var errors = new HashSet<Exception>();
        while (pending.Count != 0 && errors.Count < 16)
        {
            var current = pending.Dequeue();
            if (!errors.Add(current)) continue;
            if (current is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions.Take(16)) pending.Enqueue(inner);
            else if (current.InnerException is { } inner) pending.Enqueue(inner);
        }
        var sqlCodes = errors.OfType<SqlException>().SelectMany(e => e.Errors.Cast<SqlError>().Select(v => v.Number)).ToHashSet();
        var codes = errors.SelectMany(e => e is Win32Exception win ? new[] { win.NativeErrorCode, e.HResult } : new[] { e.HResult })
            .Concat(sqlCodes).ToHashSet();
        if (new[] { 0x800B0109u, 0x800B010Au, 0x800B010Cu, 0x80090325u, 0x800B010Fu, 0x800B0101u, 0x80090328u }
            .Any(code => codes.Contains(unchecked((int)code)))) return "Certificate";
        if (new[] { 18456, 18452, 18470, 18487, 18488 }.Any(sqlCodes.Contains)) return "Login";
        if (new[] { 229, 4060 }.Any(sqlCodes.Contains) || errors.Any(e => e is UnauthorizedAccessException)) return "Access";
        if (new[] { -2, 258 }.Any(sqlCodes.Contains) || errors.Any(e => e is TimeoutException)) return "Timeout";
        if (new[] { 26, 40, 53, 64, 233, 10060, 10061, 10054, 11001 }.Any(sqlCodes.Contains)
            || errors.Any(e => e is SocketException)) return "Connection";
        if (errors.Any(e => e is AuthenticationException)) return "Authentication";
        return "Read";
    }
}
