using SPBackup.Core.Monitoring;

namespace SPBackup.Core;

internal static class MonitoredFileCopyChecks
{
    internal static async Task<Dictionary<Guid, IReadOnlyList<MonitoredFileCopyCheck>>> ReadAsync(
        string directory, string catalogPath, IReadOnlyList<BackupJob> jobs, CancellationToken token)
    {
        var results = new Dictionary<Guid, IReadOnlyList<MonitoredFileCopyCheck>>();
        var checks = (await new BackupVerificationState(Path.Combine(directory, "verification-state.json")).ReadAsync(token))
            .ToDictionary(check => check.BackupId);
        if (checks.Count == 0) return results;

        var catalog = (await new BackupCatalog(catalogPath).ReadAsync(token)).Where(record => record.Backup.FileSnapshot is not null)
            .ToLookup(record => record.JobId);
        var remainingSlots = 1000;
        foreach (var job in jobs.Where(job => job.Kind == DatabaseKind.FilesAndFolders))
        {
            token.ThrowIfCancellationRequested();
            var jobRecords = catalog[job.Id];
            var targets = job.StorageJobs;
            if (targets.Count > remainingSlots || !jobRecords.Any(record => checks.ContainsKey(record.Id))) continue;
            var source = Path.GetFullPath(job.Source);
            var fileIdentity = FileBackupManifest.IdentifySource(source, isFile: true);
            var folderIdentity = FileBackupManifest.IdentifySource(source, isFile: false);
            var identities = targets.Select(StorageAddress.Identity).ToArray();
            var ordered = BackupPointOrder.NewestFirst(jobRecords.Where(record => identities.Contains(record.StorageIdentity, StringComparer.Ordinal)
                && (record.Backup.FileSnapshot!.SourceIdentity == fileIdentity || record.Backup.FileSnapshot.SourceIdentity == folderIdentity)), jobRecords).ToArray();
            var latest = ordered.FirstOrDefault()?.Backup.FileSnapshot;
            if (latest is null) continue;
            var pointsByTarget = ordered.Where(record => record.Backup.FileSnapshot!.SourceIdentity == latest.SourceIdentity)
                .ToLookup(record => record.StorageIdentity, StringComparer.Ordinal);
            BackupVerificationEntry? Check(BackupRecord? record) => record is not null && checks.TryGetValue(record.Id, out var check)
                && check.Matches(record) ? check : null;

            var report = identities.Select((identity, index) =>
            {
                var points = pointsByTarget[identity];
                var latestCheck = Check(points.FirstOrDefault());
                var verified = points.FirstOrDefault(record => Check(record)?.Outcome == BackupVerificationOutcome.Verified);
                var snapshot = verified?.Backup.FileSnapshot;
                return new MonitoredFileCopyCheck(index, snapshot?.SnapshotId, verified?.CompletedUtc.ToUniversalTime(),
                    Check(verified)?.CheckedUtc.ToUniversalTime(), snapshot is not null && snapshot.SnapshotId != latest.SnapshotId,
                    snapshot?.IsIncomplete == true, latestCheck?.Outcome.ToString());
            }).ToArray();
            if (!report.Any(check => check.SnapshotId is not null || check.LatestCheck is not null)) continue;
            results.Add(job.Id, report);
            remainingSlots -= report.Length;
        }
        return results;
    }
}
