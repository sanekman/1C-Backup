using System.Text.Json;

namespace SPBackup.Server;

public enum ServerEvent { Started, Stopped, StartupFailed, RequestFailed, LoginSucceeded, LoginFailed, InvitationCreated, AgentEnrolled, AgentRevoked, AdministratorPasswordChanged, PasswordChangeFailed, DiagnosticsDownloaded }
public sealed class ServerLog(ServerOptions options)
{
    private readonly object gate = new();
    private string? file;
    private int part;
    private DateOnly day;
    private readonly Guid session = Guid.NewGuid();
    public void Write(ServerEvent kind, Exception? error = null, Guid? agentId = null, string? requestId = null)
    {
        // No exception message, process output, HTTP bodies, headers, URLs, user names or tokens.
        var entry = new { Utc = DateTimeOffset.UtcNow, Event = kind.ToString(), Session = session, AgentId = agentId, RequestId = requestId,
            Version = typeof(ServerLog).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion,
            ErrorType = error?.GetType().FullName, HResult = error?.HResult, Code = (error as ServerRequestException)?.Code,
            Frames = error is null ? null : Frames(error) };
        lock (gate)
        {
            try
            {
                var directory = Path.Combine(options.DataDirectory, "logs"); Directory.CreateDirectory(directory);
                if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) return;
                var bytes = JsonSerializer.SerializeToUtf8Bytes(entry); var today = DateOnly.FromDateTime(entry.Utc.UtcDateTime);
                if (file is null || day != today || (File.Exists(file) && new FileInfo(file).Length + bytes.Length + 1 > 2 * 1024 * 1024))
                {
                    day = today; file = Path.Combine(directory, $"server-{today:yyyyMMdd}-{session:N}-{part++:D6}.jsonl");
                    var owned = Directory.EnumerateFiles(directory).Where(path => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), @"^server-[0-9]{8}-[a-f0-9]{32}-[0-9]{6}\.jsonl$"))
                        .Select(path => new FileInfo(path)).Where(info => !info.Attributes.HasFlag(FileAttributes.ReparsePoint)).OrderBy(info => info.LastWriteTimeUtc).ToArray();
                    var total = owned.Sum(info => info.Length);
                    foreach (var info in owned)
                    {
                        if (total <= 48 * 1024 * 1024 && info.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-14)) continue;
                        try { var size = info.Length; info.Delete(); total -= size; } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                }
                if (File.Exists(file) && File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) return;
                using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                stream.Write(bytes); stream.WriteByte((byte)'\n');
            }
            catch (Exception) { /* Diagnostics must not change an API transaction's result. */ }
        }
    }
    public static string[] Frames(Exception error) => new System.Diagnostics.StackTrace(error, false).GetFrames().Take(12)
        .Select(frame => (frame.GetMethod()?.DeclaringType?.FullName ?? "unknown") + "." + frame.GetMethod()?.Name + ":IL=" + frame.GetILOffset().ToString("X")).ToArray();
}
