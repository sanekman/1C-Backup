namespace OneCBackup.Core;

public enum BackupAuditStatus { Verified, Missing, Failed, Cancelled }
public sealed record BackupAuditResult(string Destination, BackupAuditStatus Status, BackupRecord? Record, string? Error,
    DateTimeOffset? CheckedUtc = null);
public sealed record BackupAuditReport(IReadOnlyList<BackupAuditResult> Destinations, bool Cancelled);

/// <summary>Reads the newest registered copy per current destination. Never creates backups or applies retention.</summary>
public sealed class LatestBackupAudit(IBackupStorage storage, BackupCatalog catalog)
{
    public async Task<BackupAuditReport> CheckAsync(BackupJob job, CancellationToken cancellationToken = default)
    {
        var errors = job.Validate();
        if (errors.Count > 0) throw new InvalidDataException(string.Join(" ", errors));
        var records = await catalog.ReadAsync(cancellationToken);
        var results = new List<BackupAuditResult>();
        var cancelled = false;
        foreach (var target in job.StorageJobs)
        {
            var destination = new BackupDestination { StorageKind = target.StorageKind,
                DestinationDirectory = target.DestinationDirectory, Ftp = target.Ftp, KeepLast = target.KeepLast }.Description;
            var identity = StorageAddress.Identity(target);
            var record = records.Where(record => record.JobId == job.Id && record.StorageIdentity == identity)
                .OrderByDescending(record => record.CompletedUtc).ThenByDescending(record => record.Id).FirstOrDefault();
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                results.Add(new(destination, BackupAuditStatus.Cancelled, record, "Проверка отменена или не начата."));
                continue;
            }
            if (record is null)
            {
                results.Add(new(destination, BackupAuditStatus.Missing, null, "В этом хранилище нет зарегистрированной копии задания."));
                continue;
            }
            try
            {
                await new BackupPointVerifier(storage, catalog).VerifyAsync(record, target, cancellationToken);
                results.Add(new(destination, BackupAuditStatus.Verified, record, null, DateTimeOffset.UtcNow));
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
