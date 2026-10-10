using System.Security.Cryptography;

namespace OneCBackup.Core;

/// <summary>Local directories and UNC shares use the same filesystem operations.</summary>
public sealed class FileBackupStorage : IBackupStorage
{
    public Task VerifyAsync(StoredBackup backup, BackupJob job, CancellationToken cancellationToken = default)
    {
        if (job.StorageKind is not (StorageKind.LocalDirectory or StorageKind.NetworkShare))
            throw new ArgumentException("Неподдерживаемое файловое хранилище.", nameof(job));
        var path = StorageAddress.ValidateLocation(job, backup);
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Проверка копии через символическую ссылку запрещена.");
        return StorageAddress.VerifyFileAsync(path, backup, cancellationToken);
    }

    public async Task DeleteAsync(StoredBackup backup, BackupJob job, CancellationToken cancellationToken = default)
    {
        if (job.StorageKind is not (StorageKind.LocalDirectory or StorageKind.NetworkShare))
            throw new ArgumentException("Неподдерживаемое файловое хранилище.", nameof(job));
        var path = StorageAddress.ValidateLocation(job, backup);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path)) return;
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Удаление копии через символическую ссылку запрещено.");
        await StorageAddress.VerifyFileAsync(path, backup, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(path);
    }

    public async Task<StoredBackup> StoreAsync(string completedBackupPath, BackupJob job,
        CancellationToken cancellationToken = default)
    {
        if (job.StorageKind is not (StorageKind.LocalDirectory or StorageKind.NetworkShare))
            throw new ArgumentException("Это хранилище поддерживает только локальные и сетевые каталоги.", nameof(job));
        var errors = job.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(job));
        cancellationToken.ThrowIfCancellationRequested();
        var source = Path.GetFullPath(completedBackupPath);
        var destination = Path.GetFullPath(Path.Combine(job.DestinationDirectory, Path.GetFileName(source)));
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = input.Length;
        var sourceHash = await SHA256.HashDataAsync(input, cancellationToken);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(source, destination, comparison))
            return new StoredBackup(destination, length, Convert.ToHexString(sourceHash));
        if (File.Exists(destination)) throw new IOException("Копия с таким именем уже существует в хранилище.");

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            input.Position = 0;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous))
            {
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            await using (var stored = File.OpenRead(temporary))
            {
                var storedHash = await SHA256.HashDataAsync(stored, cancellationToken);
                if (stored.Length != length || !CryptographicOperations.FixedTimeEquals(sourceHash, storedHash))
                    throw new InvalidDataException("Проверка сохранённой копии не пройдена.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
            return new StoredBackup(destination, length, Convert.ToHexString(sourceHash));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
