namespace SPBackup.Core;

internal static class BackupPointOrder
{
    internal static IEnumerable<BackupRecord> NewestFirst(IEnumerable<BackupRecord> points, IEnumerable<BackupRecord> jobRecords)
    {
        // Backfill can register an ancestor after a newer point. Its root keeps its
        // original position in the job, and its depth keeps it before descendants.
        var roots = jobRecords.Where(record => record.Backup.FileSnapshot is not null)
            .Select(record => record.Backup.FileSnapshot!.FullSnapshotId).Distinct()
            .Select((id, index) => (id, index)).ToDictionary(item => item.id, item => item.index);
        return points.OrderByDescending(record => record.Backup.FileSnapshot is { } snapshot
                ? roots[snapshot.FullSnapshotId] : -1)
            .ThenByDescending(record => record.Backup.FileSnapshot?.Depth ?? 0)
            .ThenByDescending(record => record.CompletedUtc)
            .ThenByDescending(record => record.Id);
    }
}
