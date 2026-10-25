using System.Security.Cryptography;

namespace SPBackup.Core;

/// <summary>Local directories and UNC shares use the same filesystem operations.</summary>
public sealed class FileBackupStorage : IBackupStorage
{
    private readonly Func<string, long?> availableBytes;
    public FileBackupStorage() : this(BackupDiskSpace.AvailableBytes) { }
    internal FileBackupStorage(Func<string, long?> availableBytes) => this.availableBytes = availableBytes;
    public Task VerifyAsync(StoredBackup backup, BackupJob job, CancellationToken cancellationToken = default)
    {
        if (job.StorageKind is not (StorageKind.LocalDirectory or StorageKind.NetworkShare))
            throw new ArgumentException("Неподдерживаемое файловое хранилище.", nameof(job));
        var path = StorageAddress.ValidateLocation(job, backup);
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Проверка копии через символическую ссылку запрещена.");
        return StorageAddress.VerifyFileAsync(path, backup, cancellationToken);
    }

    public async Task CopyToAsync(StoredBackup backup, BackupJob job, Stream destination, CancellationToken cancellationToken = default)
    {
        if (job.StorageKind is not (StorageKind.LocalDirectory or StorageKind.NetworkShare))
            throw new ArgumentException("Неподдерживаемое файловое хранилище.", nameof(job));
        var path = StorageAddress.ValidateLocation(job, backup);
        cancellationToken.ThrowIfCancellationRequested();
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Сохранение копии через символическую ссылку запрещено.");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        await input.CopyToAsync(destination, cancellationToken);
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
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var sameFile = string.Equals(source, destination, comparison);
        if (!sameFile)
        {
            if (File.Exists(destination)) throw new IOException("Копия с таким именем уже существует в хранилище.");
            cancellationToken.ThrowIfCancellationRequested();
            BackupProgressContext.Report(BackupStage.ConnectingStorage);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            BackupDiskSpace.RequireAvailable(Path.GetDirectoryName(destination)!, length, availableBytes);
        }
        BackupProgressContext.Report(BackupStage.Verifying);
        var sourceHash = await SHA256.HashDataAsync(input, cancellationToken);
        if (sameFile)
            return new StoredBackup(destination, length, Convert.ToHexString(sourceHash));
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            input.Position = 0;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous))
            {
                await BackupProgressContext.CopyAsync(input, output, BackupStage.CopyingToStorage, length, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            BackupProgressContext.Report(BackupStage.Verifying);
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
