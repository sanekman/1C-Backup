using System.Text.Json;

namespace SPBackup.Core;

public sealed record BackupRecord(Guid Id, Guid JobId, string StorageIdentity, DateTimeOffset CompletedUtc, StoredBackup Backup);

/// <summary>Persistent ownership ledger. Use one instance in the service process.</summary>
public sealed class BackupCatalog(string path)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public async Task<IReadOnlyList<BackupRecord>> ReadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { return await ReadCoreAsync(cancellationToken); }
        finally { gate.Release(); }
    }

    public async Task<BackupRecord> RegisterAsync(BackupJob job, StoredBackup backup, DateTimeOffset completedUtc,
        CancellationToken cancellationToken = default)
    {
        if (job.Validate().Count != 0) throw new ArgumentException("Некорректное задание.", nameof(job));
        StorageAddress.ValidateLocation(job, backup);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var records = await ReadCoreAsync(cancellationToken);
            var comparison = OperatingSystem.IsWindows() && (job.StorageKind is StorageKind.LocalDirectory or StorageKind.NetworkShare)
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (records.Any(r => string.Equals(r.Backup.Location, backup.Location, comparison)))
                throw new InvalidDataException("Копия с этим расположением уже зарегистрирована.");
            var record = new BackupRecord(Guid.NewGuid(), job.Id, StorageAddress.Identity(job), completedUtc.ToUniversalTime(), backup);
            records.Add(record);
            await SaveCoreAsync(records, cancellationToken);
            return record;
        }
        finally { gate.Release(); }
    }

    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var records = await ReadCoreAsync(cancellationToken);
            records.RemoveAll(r => r.Id == id);
            await SaveCoreAsync(records, cancellationToken);
        }
        finally { gate.Release(); }
    }

    private async Task<List<BackupRecord>> ReadCoreAsync(CancellationToken cancellationToken)
    {
        await using var stream = await AtomicProfileFile.OpenReadAsync(path, cancellationToken);
        if (stream is null) return [];
        var records = await JsonSerializer.DeserializeAsync<List<BackupRecord>>(stream, Options, cancellationToken)
            ?? throw new InvalidDataException("Журнал копий содержит null.");
        Validate(records);
        return records;
    }

    private static void Validate(IReadOnlyList<BackupRecord> records)
    {
        if (records.Any(r => r is null || r.Id == Guid.Empty || r.JobId == Guid.Empty ||
            string.IsNullOrWhiteSpace(r.StorageIdentity) || r.Backup is null ||
            string.IsNullOrWhiteSpace(r.Backup.Location) || r.Backup.Length < 0 || r.Backup.Sha256 is null ||
            (r.Backup.ArchivePasswordKey is not null && (string.IsNullOrWhiteSpace(r.Backup.ArchivePasswordKey)
                || r.Backup.ArchivePasswordKey.Length > 512 || r.Backup.ArchivePasswordKey.Any(char.IsControl))) ||
            (r.Backup.OriginalFileName is { } name && (string.IsNullOrWhiteSpace(name) || name.Length > 255
                || name is "." or ".." || name.Any(char.IsControl) || name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0)) ||
            r.Backup.Sha256.Length != 64 || r.Backup.Sha256.Any(c => !Uri.IsHexDigit(c))) ||
            records.Select(r => r.Id).Distinct().Count() != records.Count ||
            records.Select(r => r.Backup.Location).Distinct().Count() != records.Count)
            throw new InvalidDataException("Журнал копий содержит некорректные или повторяющиеся записи.");
        foreach (var record in records) record.Backup.FileSnapshot?.Validate();
        if (records.Where(record => record.Backup.FileSnapshot is not null)
            .GroupBy(record => (record.JobId, record.StorageIdentity, record.Backup.FileSnapshot!.SnapshotId)).Any(group => group.Count() > 1))
            throw new InvalidDataException("Журнал содержит повторяющиеся файловые снимки в одном хранилище.");
    }

    private async Task SaveCoreAsync(List<BackupRecord> records, CancellationToken cancellationToken)
    {
        Validate(records);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, records, Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            AtomicProfileFile.Publish(temporary, fullPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
