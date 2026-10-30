using System.Security.Cryptography;

namespace SPBackup.Core;

/// <summary>Local directories and UNC shares use the same filesystem operations.</summary>
public sealed class FileBackupStorage : IBackupStorage
{
    private readonly Func<string, long?> availableBytes;
    private readonly FileStorageOperations operations;
    public FileBackupStorage() : this(BackupDiskSpace.AvailableBytes) { }
    internal FileBackupStorage(Func<string, long?> availableBytes, FileStorageOperations? operations = null)
    { this.availableBytes = availableBytes; this.operations = operations ?? new(); }
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
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await StoreOpenAsync(input, source, job, cancellationToken);
    }

    // Open the source under the executor's normal identity before entering a network-only
    // logon. A remote temporary/source path must never receive the destination's NAS login.
    internal async Task<StoredBackup> StoreOpenAsync(FileStream input, string source, BackupJob job, CancellationToken cancellationToken)
    {
        var destination = Path.GetFullPath(Path.Combine(job.DestinationDirectory, Path.GetFileName(source)));
        var length = input.Length;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var sameFile = string.Equals(source, destination, comparison);
        if (sameFile)
        {
            BackupProgressContext.Report(BackupStage.Verifying);
            return new StoredBackup(destination, length, Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)));
        }
        // Never adopt a file that existed before this delivery, even if its bytes happen to match.
        if (File.Exists(destination)) throw new IOException("Копия с таким именем уже существует в хранилище.");
        byte[]? sourceHash = null;
        var temporaryFiles = new HashSet<string>();
        var publicationAttempted = false;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    BackupProgressContext.Report(BackupStage.ConnectingStorage);
                    if (File.Exists(destination))
                    {
                        // An SMB rename can succeed on the server even when its acknowledgement is lost.
                        if (!publicationAttempted || sourceHash is null)
                            throw new IOException("Копия с таким именем уже существует в хранилище.");
                        await VerifyBytesAsync(destination, sourceHash);
                        return new(destination, length, Convert.ToHexString(sourceHash));
                    }
                    publicationAttempted = false;
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    CleanupTemporaryFiles();
                    BackupDiskSpace.RequireAvailable(Path.GetDirectoryName(destination)!, length, availableBytes);
                    if (sourceHash is null)
                    {
                        BackupProgressContext.Report(BackupStage.Verifying);
                        input.Position = 0;
                        sourceHash = await SHA256.HashDataAsync(input, cancellationToken);
                    }
                    input.Position = 0;
                    var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    // Ownership starts only after CreateNew succeeds; a collision is never deleted.
                    cancellationToken.ThrowIfCancellationRequested();
                    await using (var output = operations.OpenTemporary(temporary))
                    {
                        temporaryFiles.Add(temporary);
                        await BackupProgressContext.CopyAsync(input, output, BackupStage.CopyingToStorage, length, cancellationToken);
                        await output.FlushAsync(cancellationToken);
                        output.Flush(flushToDisk: true);
                    }
                    await VerifyBytesAsync(temporary, sourceHash);
                    cancellationToken.ThrowIfCancellationRequested();
                    publicationAttempted = true;
                    operations.Publish(temporary, destination);
                    return new(destination, length, Convert.ToHexString(sourceHash));
                }
                catch (IOException error) when (attempt < 2 && !cancellationToken.IsCancellationRequested && FileStorageFailures.Transient(error))
                {
                    DiagnosticLog.Current.Write(DiagnosticEvent.StorageRetrying, DiagnosticLevel.Warning, error,
                        jobId: job.Id, storageKind: job.StorageKind, values: new(Count: attempt + 1, Code: FileStorageFailures.Code(error)));
                    BackupProgressContext.Report(BackupStage.RetryingStorage);
                    await operations.Delay(attempt + 1, cancellationToken);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException && FileStorageFailures.Hint(error) is not null)
        {
            var hint = FileStorageFailures.Hint(error) + $" Код Windows: {FileStorageFailures.Code(error)}.";
            if (error is UnauthorizedAccessException) throw new UnauthorizedAccessException(hint, error);
            throw new IOException(hint, error);
        }
        finally { CleanupTemporaryFiles(); }

        async Task VerifyBytesAsync(string path, byte[] expectedHash)
        {
            BackupProgressContext.Report(BackupStage.Verifying);
            FileBackupPaths.RejectReparse(path);
            await using var stored = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            var storedHash = await SHA256.HashDataAsync(stored, cancellationToken);
            if (stored.Length != length || !CryptographicOperations.FixedTimeEquals(expectedHash, storedHash))
                throw new InvalidDataException("Проверка сохранённой копии не пройдена.");
        }
        void CleanupTemporaryFiles()
        {
            foreach (var path in temporaryFiles.ToArray())
            {
                try { operations.DeleteTemporary(path); temporaryFiles.Remove(path); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { DiagnosticLog.Current.Write(DiagnosticEvent.StorageTemporaryCleanupFailed, DiagnosticLevel.Warning, error, jobId: job.Id, storageKind: job.StorageKind); }
            }
        }
    }
}
