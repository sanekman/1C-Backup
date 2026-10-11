using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OneCBackup.Core;

public enum DiagnosticComponent { Desktop, Agent, Tests }
public enum DiagnosticLevel { Info, Warning, Error }
public enum DiagnosticEvent
{
    ProcessStarted, ProcessStopped, ProcessError, UnhandledException, UnobservedTaskException, DesktopError,
    RunStarted, SourcePreparationStarted, SourcePreparationCompleted, BackupCreationStarted, BackupCreationCompleted,
    BackupDeliveryStarted, DestinationDeliveryStarted, DestinationStored, DestinationRegistered, DestinationFailed,
    RetentionStarted, RetentionCompleted, RetentionFailed, RunFinished, RunFailed, RunCancelled, RunInterrupted,
    WorkCleanupFailed, SnapshotCacheFailed, SourceEnumerationStarted, SourceEnumerated, SourceRevalidated,
    WebDavTransfersCompleted, WebDavRequestFailed, AgentStarted, AgentStopRequested, AgentStopped, AgentFailed,
    MonitoringFailed, MonitoringRecovered, SourceCheckStarted, SourceCheckCompleted, SourceCheckFailed,
    FileRestoreStarted, FileRestoreCompleted, FileRestoreFailed, FileRestoreCancelled,
    CopyVerificationStarted, CopyVerificationCompleted, CopyVerificationFailed,
    ExternalProcessStarted, ExternalProcessExited, ExternalProcessFailed,
    DiagnosticExportStarted, DiagnosticExportCompleted, DiagnosticExportFailed
}
public sealed record DiagnosticValues(long? Count = null, long? Bytes = null, long? Milliseconds = null, int? Code = null);

// No Message, ToString(), Data, source paths or raw process output: those can contain passwords and connection strings.
public sealed record DiagnosticException(string Type, int HResult, int? NativeCode, IReadOnlyList<string> Frames,
    IReadOnlyList<DiagnosticException> Inner)
{
    public static DiagnosticException Capture(Exception error, int depth = 0)
    {
        var frames = new StackTrace(error, false).GetFrames().Take(16).Select(frame =>
        {
            var method = frame.GetMethod();
            return Limit((method?.DeclaringType?.FullName ?? "unknown") + "." + method?.Name + ":IL=" + frame.GetILOffset().ToString("X"), 512);
        }).ToArray();
        var inner = depth >= 3 ? [] : error is AggregateException aggregate
            ? aggregate.InnerExceptions.Take(4).Select(item => Capture(item, depth + 1)).ToArray()
            : error.InnerException is { } nested ? new[] { Capture(nested, depth + 1) } : [];
        int? code = error switch
        {
            System.ComponentModel.Win32Exception win => win.NativeErrorCode,
            Microsoft.Data.SqlClient.SqlException sql => sql.Number,
            System.Net.Http.HttpRequestException http when http.StatusCode is { } status => (int)status,
            _ => null
        };
        return new(Limit(error.GetType().FullName ?? "Exception", 512), error.HResult, code, frames, inner);
    }
    private static string Limit(string value, int limit) => value.Length > limit ? value[..limit] : value;
}
public sealed record DiagnosticEntry(int FormatVersion, DateTimeOffset Utc, DiagnosticLevel Level,
    DiagnosticComponent Component, DiagnosticEvent Event, Guid SessionId, int ProcessId, string Version, string Revision,
    Guid? JobId, Guid? RunId, DatabaseKind? SourceKind, StorageKind? StorageKind, int? DestinationIndex,
    BackupRunStatus? Outcome, DiagnosticValues? Values, DiagnosticException? Error);

public sealed record DiagnosticPolicy(int Days = 14, long MaximumFileBytes = 2 * 1024 * 1024,
    long MaximumTotalBytes = 50 * 1024 * 1024);

/// <summary>Best-effort structured logging. A failed logger never changes backup execution or terminal history.</summary>
public sealed class DiagnosticLog
{
    private sealed record Ambient(DiagnosticLog Log, Guid? JobId, Guid? RunId, DatabaseKind? SourceKind);
    private static readonly AsyncLocal<Ambient?> ambient = new();
    private static DiagnosticLog? processLog;
    private static readonly DiagnosticLog disabled = new();
    internal static readonly JsonSerializerOptions Json = new()
    { Converters = { new JsonStringEnumConverter() }, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly Regex OwnName = new(@"^(Desktop|Agent|Tests)-[0-9]{8}-[0-9]+-[a-f0-9]{32}-[0-9]{4}\.jsonl$", RegexOptions.CultureInvariant);
    private readonly object gate = new();
    private readonly DiagnosticPolicy policy = new();
    private readonly Guid sessionId = Guid.NewGuid();
    private readonly DiagnosticComponent component;
    private readonly string version = "unknown", revision = "unknown";
    private string? currentFile;
    private DateOnly currentDay;
    private int part;
    private DateTimeOffset lastCleanup;
    public string? DirectoryPath { get; }
    public DiagnosticException? LastWriteFailure { get; private set; }
    public static DiagnosticLog Current => ambient.Value?.Log ?? processLog ?? disabled;
    private DiagnosticLog() { }
    public DiagnosticLog(string profileDirectory, DiagnosticComponent component, string version, string revision,
        DiagnosticPolicy? policy = null)
    {
        DirectoryPath = Path.Combine(Path.GetFullPath(profileDirectory), "logs");
        if (!Enum.IsDefined(component)) throw new ArgumentException("Неизвестный компонент диагностики.");
        this.component = component; this.version = SafeBuild(version); this.revision = SafeBuild(revision);
        this.policy = policy ?? new();
        if (this.policy.Days is < 1 or > 90 || this.policy.MaximumFileBytes is < 4096 or > 32 * 1024 * 1024
            || this.policy.MaximumTotalBytes < this.policy.MaximumFileBytes || this.policy.MaximumTotalBytes > 100 * 1024 * 1024)
            throw new ArgumentException("Некорректные ограничения журнала диагностики.");
    }
    private static string SafeBuild(string value) => value.Length is > 0 and <= 128 && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '+') ? value : "unknown";
    public static DiagnosticLog Start(string profileDirectory, DiagnosticComponent component, Assembly assembly)
    {
        var build = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var parts = build.Split('+', 2);
        var log = new DiagnosticLog(profileDirectory, component, parts[0], parts.Length == 2 ? parts[1] : "unknown");
        processLog = log; log.Write(DiagnosticEvent.ProcessStarted); return log;
    }
    public static IDisposable Use(DiagnosticLog log, Guid? jobId = null, Guid? runId = null, DatabaseKind? sourceKind = null)
    {
        var previous = ambient.Value; ambient.Value = new(log, jobId, runId, sourceKind);
        return new Scope(() => ambient.Value = previous);
    }
    private sealed class Scope(Action restore) : IDisposable { public void Dispose() => restore(); }
    public void Write(DiagnosticEvent kind, DiagnosticLevel level = DiagnosticLevel.Info, Exception? error = null,
        Guid? jobId = null, Guid? runId = null, DatabaseKind? sourceKind = null, StorageKind? storageKind = null,
        int? destinationIndex = null, BackupRunStatus? outcome = null, DiagnosticValues? values = null)
    {
        if (DirectoryPath is null) return;
        lock (gate)
        {
            try
            {
                var context = ambient.Value;
                var entry = new DiagnosticEntry(1, DateTimeOffset.UtcNow, level, component, kind, sessionId, Environment.ProcessId,
                    version, revision, jobId ?? context?.JobId, runId ?? context?.RunId, sourceKind ?? context?.SourceKind,
                    storageKind, destinationIndex, outcome, values, error is null ? null : DiagnosticException.Capture(error));
                var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, Json);
                if (bytes.Length > 32768 || bytes.Length + 1 > policy.MaximumFileBytes) throw new InvalidDataException("Слишком большое событие диагностики.");
                Directory.CreateDirectory(DirectoryPath); RequireRealDirectory(DirectoryPath);
                var day = DateOnly.FromDateTime(entry.Utc.UtcDateTime);
                if (currentFile is null || day != currentDay || new FileInfo(currentFile).LengthIfExists() + bytes.Length + 1 > policy.MaximumFileBytes)
                {
                    if (day != currentDay) { currentDay = day; part = 0; }
                    currentFile = Path.Combine(DirectoryPath, $"{component}-{day:yyyyMMdd}-{Environment.ProcessId}-{sessionId:N}-{part++:D4}.jsonl");
                    CleanupCore(entry.Utc);
                }
                if (File.Exists(currentFile) && File.GetAttributes(currentFile).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("Ссылка вместо журнала диагностики.");
                using (var file = new FileStream(currentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                { file.Write(bytes); file.WriteByte((byte)'\n'); }
                if (entry.Utc - lastCleanup > TimeSpan.FromMinutes(5)) CleanupCore(entry.Utc);
                LastWriteFailure = null;
            }
            catch (Exception ex) { LastWriteFailure = DiagnosticException.Capture(ex); }
        }
    }
    internal static bool IsOwnFile(string name) => OwnName.IsMatch(name);
    internal static void RequireRealDirectory(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Ссылка вместо каталога диагностики.");
    }
    private void CleanupCore(DateTimeOffset now)
    {
        lastCleanup = now;
        var files = Directory.EnumerateFiles(DirectoryPath!).Where(path => IsOwnFile(Path.GetFileName(path)))
            .Select(path => new FileInfo(path)).Where(file => !file.Attributes.HasFlag(FileAttributes.ReparsePoint)).OrderBy(file => file.LastWriteTimeUtc).ToList();
        long total = files.Sum(file => file.Length);
        foreach (var file in files)
        {
            if (file.FullName == currentFile) continue;
            if (file.LastWriteTimeUtc >= now.UtcDateTime.AddDays(-policy.Days) && total <= policy.MaximumTotalBytes - policy.MaximumFileBytes) continue;
            try { var length = file.Length; file.Delete(); total -= length; }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
internal static class DiagnosticFileInfoExtensions
{ internal static long LengthIfExists(this FileInfo file) => file.Exists ? file.Length : 0; }
