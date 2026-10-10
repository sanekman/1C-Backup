using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneCBackup.Core;

// The caller owns synchronization. A temporary file in the same directory avoids
// exposing a partial JSON document when writing fails or the process stops.
public sealed class JobStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<IReadOnlyList<BackupJob>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return [];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var jobs = await JsonSerializer.DeserializeAsync<List<BackupJob>>(stream, Options, cancellationToken)
            ?? throw new InvalidDataException("Файл заданий содержит null.");
        ValidateJobs(jobs);
        return jobs;
    }

    public async Task SaveAsync(IReadOnlyList<BackupJob> jobs, CancellationToken cancellationToken = default)
    {
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
            File.Move(temporary, fullPath, overwrite: true);
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
