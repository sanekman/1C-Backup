namespace OneCBackup.Core;

/// <summary>Archives locally before storage upload. The original backup is always retained.</summary>
public sealed class BackupDelivery(IBackupStorage storage)
{
    public async Task<StoredBackup> DeliverAsync(BackupJob job, string completedBackupPath,
        string stagingDirectory, CancellationToken cancellationToken = default)
    {
        if (!job.ShouldArchiveCopy)
            return await storage.StoreAsync(completedBackupPath, job, cancellationToken);

        var workDirectory = Path.Combine(Path.GetFullPath(stagingDirectory), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        try
        {
            var archive = await new BackupArchiver().CreateZipAsync(completedBackupPath,
                Path.Combine(workDirectory, Path.GetFileName(completedBackupPath) + ".zip"),
                cancellationToken: cancellationToken);
            return await storage.StoreAsync(archive, job, cancellationToken);
        }
        finally { Directory.Delete(workDirectory, recursive: true); }
    }
}
