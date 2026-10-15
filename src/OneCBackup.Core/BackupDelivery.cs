namespace OneCBackup.Core;

/// <summary>Archives locally before storage upload. The original backup is always retained.</summary>
public sealed class BackupDelivery(IBackupStorage storage, IArchivePasswordStore? passwords = null)
{
    public async Task<StoredBackup> DeliverAsync(BackupJob job, string completedBackupPath,
        string stagingDirectory, CancellationToken cancellationToken = default)
    {
        using var prepared = await PrepareFileAsync(job, completedBackupPath, stagingDirectory, cancellationToken);
        return (await storage.StoreAsync(prepared.Path, job, cancellationToken)) with
        { ArchivePasswordKey = job.ArchivePasswordKey, FileSnapshot = prepared.FileSnapshot };
    }

    internal async Task<PreparedBackup> PrepareFileAsync(BackupJob job, string completedBackupPath,
        string stagingDirectory, CancellationToken cancellationToken)
    {
        var password = await ArchivePasswords.ResolveAsync(job.ArchivePasswordKey, passwords, cancellationToken);
        if (!job.ShouldArchiveCopy)
        {
            if (job.IsFileSnapshot)
            {
                var manifest = await FileBackupZip.ReadManifestAsync(completedBackupPath, password, cancellationToken);
                return new PreparedBackup(completedBackupPath, null) { FileSnapshot = manifest.GetSnapshot() };
            }
            if (password is not null && !(job.Kind == DatabaseKind.OneCFile && job.OneCMethod == OneCBackupMethod.FileArchive))
                throw new InvalidDataException("Для шифрования требуется архивация в ZIP.");
            if (password is not null)
            {
                using var zip = new ICSharpCode.SharpZipLib.Zip.ZipFile(completedBackupPath) { Password = password };
                if (zip.Count != 1 || zip[0].Name != "1Cv8.1CD" || zip[0].AESKeySize != 256 || !zip[0].IsCrypted)
                    throw new InvalidDataException("Исполнитель не создал зашифрованный ZIP файла базы.");
                await using var content = zip.GetInputStream(0);
                await System.Security.Cryptography.SHA256.HashDataAsync(content, cancellationToken);
            }
            // Native file-base executor already creates and verifies its encrypted ZIP.
            return new PreparedBackup(completedBackupPath, null);
        }

        var workDirectory = Path.Combine(Path.GetFullPath(stagingDirectory), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        try
        {
            var archive = await new BackupArchiver().CreateZipAsync(completedBackupPath,
                Path.Combine(workDirectory, Path.GetFileName(completedBackupPath) + ".zip"),
                cancellationToken: cancellationToken, password: password);
            return new PreparedBackup(archive, workDirectory);
        }
        catch { Directory.Delete(workDirectory, recursive: true); throw; }
    }
}

internal sealed record PreparedBackup(string Path, string? OwnedDirectory) : IDisposable
{
    internal FileBackupSnapshot? FileSnapshot { get; init; }
    public void Dispose() { if (OwnedDirectory is not null) Directory.Delete(OwnedDirectory, recursive: true); }
}
