using System.Text.Json;

namespace OneCBackup.Core;

public enum BackupStage { ConnectingSource, PreparingStorage, CreatingBackup, ScanningSource, ProcessingFiles, DownloadingFile, ArchivingFile, Archiving, ConnectingStorage, CopyingToStorage, Verifying, Retention, Waiting, Completed, Failed, Cancelled, PartiallySucceeded, ArchivingFiles, DownloadingAndArchivingFiles }
public sealed record BackupStageProgress(BackupStage Stage, long Completed = 0, long Total = 0)
{
    public int? Percent => Total > 0 ? (int)Math.Clamp(100d * Completed / Total, 0, 100) : Stage == BackupStage.Completed ? 100 : null;
}
public sealed record BackupDestinationProgress(int Index, StorageKind Kind, BackupStageProgress Progress);
public sealed record BackupProgressSnapshot(Guid RunId, Guid JobId, string JobName, int ProcessId, long Sequence,
    DateTimeOffset UpdatedUtc, BackupStageProgress Progress, IReadOnlyList<BackupDestinationProgress> Destinations)
{
    public bool Finished => Progress.Stage is BackupStage.Completed or BackupStage.Failed or BackupStage.Cancelled or BackupStage.PartiallySucceeded;
}

/// <summary>One ambient scope per run; child deliveries retain their own destination slot.</summary>
public static class BackupProgressContext
{
    private sealed record Slot(Session Session, int? Destination);
    private static readonly AsyncLocal<Slot?> current = new();
    public static IDisposable Begin(BackupJob job, Guid runId, Action<BackupProgressSnapshot>? observer)
    {
        var previous = current.Value;
        var session = new Session(job, runId, observer);
        current.Value = new(session, null);
        Report(BackupStage.ConnectingSource);
        return new Scope(() => { if (!session.Finished) Report(BackupStage.Failed); current.Value = previous; });
    }
    public static IDisposable Destination(int index)
    {
        var previous = current.Value;
        if (previous is not null) current.Value = previous with { Destination = index };
        return new Scope(() => current.Value = previous);
    }
    public static void Report(BackupStage stage, long completed = 0, long total = 0)
    {
        var slot = current.Value;
        slot?.Session.Report(slot.Destination, new(stage, Math.Max(0, completed), Math.Max(0, total)));
    }
    public static IProgress<long> Bytes(BackupStage stage, long total, IProgress<long>? other = null)
    {
        var slot = current.Value;
        return new InlineProgress<long>(bytes => { slot?.Session.Report(slot.Destination, new(stage, Math.Max(0, bytes), Math.Max(0, total))); other?.Report(bytes); });
    }
    public static async Task CopyAsync(Stream input, Stream output, BackupStage stage, long total, CancellationToken token, long completedBefore = 0)
    {
        Report(stage, completedBefore, total);
        var buffer = new byte[81920]; long completed = completedBefore; int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            completed += read; Report(stage, completed, total);
        }
    }
    private sealed class Scope(Action end) : IDisposable { public void Dispose() => end(); }
    private sealed class Session(BackupJob job, Guid runId, Action<BackupProgressSnapshot>? observer)
    {
        private readonly object gate = new();
        private BackupStageProgress progress = new(BackupStage.ConnectingSource);
        private readonly Dictionary<int, BackupDestinationProgress> destinations = new();
        private long sequence;
        private DateTimeOffset last;
        public bool Finished { get; private set; }
        public void Report(int? destination, BackupStageProgress value)
        {
            lock (gate)
            {
                if (Finished) return;
                var changed = destination is int index
                    ? !destinations.TryGetValue(index, out var prior) || prior.Progress.Stage != value.Stage
                    : progress.Stage != value.Stage;
                if (destination is int i) destinations[i] = new(i, job.StorageJobs[i - 1].StorageKind, value);
                else progress = value;
                Finished = progress.Stage is BackupStage.Completed or BackupStage.Cancelled or BackupStage.Failed or BackupStage.PartiallySucceeded;
                var now = DateTimeOffset.UtcNow;
                if (!changed && !Finished && now - last < TimeSpan.FromMilliseconds(500) && !(value.Total > 0 && value.Completed >= value.Total)) return;
                last = now;
                // A broken UI or progress file must never fail an otherwise valid backup.
                try { observer?.Invoke(new(runId, job.Id, job.Name, Environment.ProcessId, ++sequence, now, progress, destinations.Values.OrderBy(d => d.Index).ToArray())); }
                catch (Exception) { }
            }
        }
    }
}
public sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }

/// <summary>Atomic profile-local snapshot allows the desktop to observe the separate agent.</summary>
public sealed class BackupProgressStore(string directory)
{
    private readonly string path = Path.Combine(directory, "backup-progress.json");
    public void Write(BackupProgressSnapshot snapshot)
    {
        Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot)); AtomicProfileFile.Publish(temporary, path); }
        finally { File.Delete(temporary); }
    }
    public async Task<BackupProgressSnapshot?> ReadAsync()
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return await JsonSerializer.DeserializeAsync<BackupProgressSnapshot>(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
