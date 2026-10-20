namespace OneCBackup.Core;

internal static class FileBackupBases
{
    internal static async Task<FileBackupManifest?> ReadAsync(BackupJob job, string sourceIdentity, BackupCatalog catalog, FileBackupSnapshotStore snapshots, CancellationToken token)
    {
        FileBackupManifest? previous = null;
        if (job.FileBackupMode == FileBackupMode.Incremental)
        {
            try { previous = await snapshots.ReadAsync(job.Id, token); }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { previous = null; }
            if (previous is not null && (previous.SourceIdentity != sourceIdentity || previous.Depth + 1 >= job.FullBackupEvery)) previous = null;
            if (previous is not null)
            {
                var records = await catalog.ReadAsync(token);
                var previousSnapshot = previous.GetSnapshot();
                foreach (var target in job.StorageJobs)
                {
                    var newest = records.Where(record => record.JobId == job.Id && record.StorageIdentity == StorageAddress.Identity(target))
                        .OrderByDescending(record => record.CompletedUtc).ThenByDescending(record => record.Id).FirstOrDefault();
                    if (newest?.Backup.FileSnapshot != previousSnapshot || newest.Backup.ArchivePasswordKey != job.ArchivePasswordKey)
                    { previous = null; break; }
                    try { FileBackupChains.Resolve(newest, records); }
                    catch (InvalidDataException) { previous = null; break; }
                }
            }
        }
        return previous;
    }
}
