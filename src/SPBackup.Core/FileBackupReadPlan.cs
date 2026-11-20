using System.ComponentModel;

namespace SPBackup.Core;

internal sealed class FileBackupReadPlan
{
    private readonly ILookup<Guid, BackupRecord> alternatives;
    public IReadOnlyList<BackupRecord> Chain { get; }

    internal FileBackupReadPlan(BackupRecord selected, BackupJob job, IReadOnlyList<BackupRecord> records, bool allowOtherDestinations)
    {
        BackupCopyVerifier.GetStorageJob(selected, job);
        if (!records.Contains(selected)) throw new InvalidDataException("Выбранная копия больше не зарегистрирована.");
        Chain = allowOtherDestinations
            ? FileBackupChains.ResolveAcrossStorages(selected, job, records, checkLocalAvailability: true)
            : FileBackupChains.Resolve(selected, records);
        var targets = allowOtherDestinations ? job.StorageJobs.Select(StorageAddress.Identity).ToHashSet(StringComparer.Ordinal) : [];
        alternatives = records.Where(record => record.JobId == job.Id
            && record.Backup.FileSnapshot is not null && targets.Contains(record.StorageIdentity))
            .ToLookup(record => record.Backup.FileSnapshot!.SnapshotId);
    }

    internal async Task ExportAsync(BackupRecord expected, BackupJob job, BackupCopyExporter exporter, string path,
        CancellationToken token, IProgress<BackupExportProgress>? progress)
    {
        var candidates = alternatives[expected.Backup.FileSnapshot!.SnapshotId]
            .Where(record => record.Id != expected.Id && FileBackupChains.EquivalentArchive(expected, record))
            .Prepend(expected).ToArray();
        for (var index = 0; index < candidates.Length; index++)
        {
            try
            {
                await exporter.ExportAsync(candidates[index], job, path, token, progress);
                return;
            }
            catch (Exception error) when (index + 1 < candidates.Length && !token.IsCancellationRequested
                && error is IOException or UnauthorizedAccessException or InvalidDataException or HttpRequestException or Win32Exception)
            {
                // An equivalent registered archive in another permitted storage can be read after a failed attempt.
            }
        }
    }
}
