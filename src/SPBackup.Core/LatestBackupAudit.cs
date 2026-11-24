namespace SPBackup.Core;

public enum BackupAuditStatus { Verified, Missing, Failed, Cancelled }
public sealed record BackupAuditResult(string Destination, BackupAuditStatus Status, BackupRecord? Record, string? Error,
    DateTimeOffset? CheckedUtc = null)
{
    public bool OlderThanLatestPoint { get; init; }
}
public sealed record BackupAuditReport(IReadOnlyList<BackupAuditResult> Destinations, bool Cancelled);

/// <summary>Reads the newest registered copy per current destination. Never creates backups or applies retention.</summary>
public sealed class LatestBackupAudit(IBackupStorage storage, BackupCatalog catalog)
{
    public async Task<BackupAuditReport> CheckAsync(BackupJob job, CancellationToken cancellationToken = default)
    {
        var errors = job.Validate();
        if (errors.Count > 0) throw new InvalidDataException(string.Join(" ", errors));
        var records = (await catalog.ReadAsync(cancellationToken)).Where(record => record.JobId == job.Id).ToArray();
        var pathSource = job.Kind == DatabaseKind.FilesAndFolders ? Path.GetFullPath(job.Source) : null;
        var fileIdentity = pathSource is null || Directory.Exists(pathSource) ? null : FileBackupManifest.IdentifySource(pathSource, isFile: true);
        var directoryIdentity = pathSource is null || File.Exists(pathSource) ? null : FileBackupManifest.IdentifySource(pathSource, isFile: false);
        var destinations = job.StorageJobs;
        var identities = destinations.Select(StorageAddress.Identity).ToHashSet(StringComparer.Ordinal);
        var ordered = BackupPointOrder.NewestFirst(records.Where(record => identities.Contains(record.StorageIdentity)
            && (record.Backup.FileSnapshot is not null) == job.IsFileSnapshot
            && (pathSource is null || record.Backup.FileSnapshot is { } snapshot
                && (snapshot.SourceIdentity == fileIdentity || snapshot.SourceIdentity == directoryIdentity))), records).ToArray();
        var latest = ordered.FirstOrDefault()?.Backup.FileSnapshot;
        var results = new List<BackupAuditResult>();
        var cancelled = false;
        foreach (var target in destinations)
        {
            var destination = new BackupDestination { StorageKind = target.StorageKind,
                DestinationDirectory = target.DestinationDirectory, Ftp = target.Ftp, GoogleDrive = target.GoogleDrive, KeepLast = target.KeepLast }.Description;
            var identity = StorageAddress.Identity(target);
            var record = ordered.FirstOrDefault(item => item.StorageIdentity == identity);
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                results.Add(new(destination, BackupAuditStatus.Cancelled, record, "Проверка отменена или не начата."));
                continue;
            }
            if (record is null)
            {
                results.Add(new(destination, BackupAuditStatus.Missing, null, pathSource is null ? "В этом хранилище нет зарегистрированной копии задания."
                    : "Для текущего источника ещё нет копии в этом хранилище."));
                continue;
            }
            try
            {
                await new BackupPointVerifier(storage, catalog).VerifyAsync(record, target, cancellationToken);
                results.Add(new(destination, BackupAuditStatus.Verified, record, null, DateTimeOffset.UtcNow)
                {
                    OlderThanLatestPoint = latest is not null && record.Backup.FileSnapshot?.SnapshotId != latest.SnapshotId
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                results.Add(new(destination, BackupAuditStatus.Cancelled, record, "Проверка отменена.", DateTimeOffset.UtcNow));
            }
            catch (Exception ex) { results.Add(new(destination, BackupAuditStatus.Failed, record, ex.Message, DateTimeOffset.UtcNow)); }
        }
        return new(results, cancelled);
    }
}
