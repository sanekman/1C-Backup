using System.Text.Json;
using System.Text.Json.Serialization;

namespace SPBackup.Core;

// The caller owns synchronization. A temporary file in the same directory avoids
// exposing a partial JSON document when writing fails or the process stops.
public sealed class JobStore(string path, bool useLocalTime = false)
{
    internal string StartupSchedulePath => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "startup-schedule.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public Task<IReadOnlyList<BackupJob>> LoadAsync(CancellationToken cancellationToken = default) => LoadSnapshotAsync(cancellationToken, 0);

    private async Task<IReadOnlyList<BackupJob>> LoadSnapshotAsync(CancellationToken cancellationToken, int attempt)
    {
        var pending = await ReadProtectedAsync(PendingPath, cancellationToken, snapshot: true);
        if (pending is not null) return Normalize(pending);
        var before = await ReadProtectedAsync(ProtectedPath, cancellationToken) ?? [];
        await using var stream = await AtomicProfileFile.OpenReadAsync(path, cancellationToken);
        var jobs = stream is null ? [] : await JsonSerializer.DeserializeAsync<List<BackupJob>>(stream, Options, cancellationToken)
            ?? throw new InvalidDataException("Файл заданий содержит null.");
        var secured = await ReadProtectedAsync(ProtectedPath, cancellationToken) ?? [];
        pending = await ReadProtectedAsync(PendingPath, cancellationToken, snapshot: true);
        if (pending is not null) return Normalize(pending);
        if (JsonSerializer.Serialize(before, Options) != JsonSerializer.Serialize(secured, Options))
        {
            if (attempt >= 3) throw new IOException("Настройки заданий изменяются. Повторите чтение.");
            return await LoadSnapshotAsync(cancellationToken, attempt + 1);
        }
        var ids = secured.Select(job => job.Id).ToHashSet();
        return Normalize(jobs.Where(job => job is null || !ids.Contains(job.Id)).Concat(secured).ToArray());
    }

    private static readonly JsonSerializerOptions ProtectedOptions = new(Options)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private string ProtectedPath => Path.GetFullPath(path) + ".protected.json";
    private string PendingPath => Path.GetFullPath(path) + ".pending.json";
    private sealed record ProtectedDocument(int FormatVersion, IReadOnlyList<BackupJob> Jobs);
    private static bool Protected(BackupJob job) => job.Kind == DatabaseKind.PostgreSql && job.PostgreSql?.Tls is not null;
    private IReadOnlyList<BackupJob> Normalize(IReadOnlyList<BackupJob> jobs)
    {
        var normalized = useLocalTime ? jobs.Select(job => job is null ? null! : job with { TimeZoneId = BackupSchedule.LocalTimeZone }).ToArray() : jobs;
        ValidateJobs(normalized);
        return normalized;
    }
    private static async Task<IReadOnlyList<BackupJob>?> ReadProtectedAsync(string file, CancellationToken token, bool snapshot = false)
    {
        await using var stream = await AtomicProfileFile.OpenReadAsync(file, token);
        if (stream is null) return null;
        var value = await JsonSerializer.DeserializeAsync<ProtectedDocument>(stream, ProtectedOptions, token)
            ?? throw new InvalidDataException("Файл защищённых заданий содержит null.");
        if (value.FormatVersion != 1 || value.Jobs is null || !snapshot && value.Jobs.Any(job => job is null || !Protected(job)))
            throw new InvalidDataException("Повреждён файл защищённых заданий. Восстановите профиль; запуск без защиты запрещён.");
        ValidateJobs(value.Jobs);
        return value.Jobs;
    }
    public async Task SaveAsync(IReadOnlyList<BackupJob> jobs, CancellationToken cancellationToken = default)
    {
        jobs = Normalize(jobs);
        var secured = jobs.Where(Protected).ToArray();
        var previous = File.Exists(PendingPath) ? [] : await ReadProtectedAsync(ProtectedPath, cancellationToken) ?? [];
        var ordinary = jobs.Where(job => !Protected(job)).ToArray();
        if (!File.Exists(PendingPath) && JsonSerializer.Serialize(previous, Options) == JsonSerializer.Serialize(secured, Options))
        {
            await SaveLegacyAsync(ordinary, cancellationToken); return;
        }
        // An old executor must be stopped before a job is moved out of its legacy file.
        using var lease = new BackupEngineCoordinator(Path.GetDirectoryName(Path.GetFullPath(path))!).Acquire();
        await new AgentJsonFile<ProtectedDocument>(PendingPath).WriteAsync(new(1, jobs), cancellationToken);
        // From this publication onward readers see the complete pending snapshot. Finish without
        // cancellation; an interrupted commit is recoverable from that durable snapshot.
        try { await SaveLegacyAsync(ordinary, CancellationToken.None); }
        catch { File.Delete(PendingPath); throw; }
        await new AgentJsonFile<ProtectedDocument>(ProtectedPath).WriteAsync(new(1, secured), CancellationToken.None);
        File.Delete(PendingPath);
    }

    private async Task SaveLegacyAsync(IReadOnlyList<BackupJob> jobs, CancellationToken cancellationToken)
    {
        if (useLocalTime) jobs = jobs.Select(job => job is null ? null! : job with { TimeZoneId = BackupSchedule.LocalTimeZone }).ToArray();
        ValidateJobs(jobs);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, jobs, Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            AtomicProfileFile.Publish(temporary, fullPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void ValidateJobs(IReadOnlyList<BackupJob> jobs)
    {
        if (jobs.Any(j => j is null)) throw new InvalidDataException("Файл содержит пустое задание.");
        var errors = jobs.SelectMany(j => j.Validate()).ToArray();
        if (errors.Length > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        if (jobs.Select(j => j.Id).Distinct().Count() != jobs.Count)
            throw new InvalidDataException("Идентификаторы заданий должны быть уникальными.");
    }
}
