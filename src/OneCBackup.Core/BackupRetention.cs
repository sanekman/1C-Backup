namespace OneCBackup.Core;

public sealed record RetentionFailure(Guid BackupId, Exception Error);
public sealed record RetentionResult(IReadOnlyList<Guid> Deleted, IReadOnlyList<RetentionFailure> Failures);

public sealed class BackupRetention(BackupCatalog catalog, IBackupStorage storage)
{
    public async Task<RetentionResult> ApplyAsync(BackupJob job, BackupRecord newest,
        CancellationToken cancellationToken = default)
    {
        if (job.KeepLast < 1) throw new ArgumentException("Нужно сохранять хотя бы одну копию.", nameof(job));
        var identity = StorageAddress.Identity(job);
        var records = await catalog.ReadAsync(cancellationToken);
        if (newest.JobId != job.Id || newest.StorageIdentity != identity || !records.Contains(newest))
            throw new InvalidDataException("Очистка разрешена только после регистрации новой успешной копии этого задания.");
        await storage.VerifyAsync(newest.Backup, job, cancellationToken);
        var owned = records.Where(r => r.JobId == job.Id && r.StorageIdentity == identity).ToArray();
        var keep = owned.Where(r => r.Id != newest.Id).OrderByDescending(r => r.CompletedUtc).ThenByDescending(r => r.Id)
            .Take(job.KeepLast - 1).Select(r => r.Id).Append(newest.Id).ToHashSet();
        var deleted = new List<Guid>();
        var failures = new List<RetentionFailure>();
        foreach (var record in owned.Where(r => !keep.Contains(r.Id)).OrderBy(r => r.CompletedUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await storage.DeleteAsync(record.Backup, job, cancellationToken);
                await catalog.RemoveAsync(record.Id, cancellationToken);
                deleted.Add(record.Id);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { failures.Add(new RetentionFailure(record.Id, ex)); }
        }
        return new RetentionResult(deleted, failures);
    }
}

public sealed record CompletedBackup(BackupRecord Record, RetentionResult Retention);

/// <summary>One coordinator per service: deliver, register success, then apply retention.</summary>
public sealed class BackupCompletion(IBackupStorage storage, BackupCatalog catalog)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public Task PrepareAsync(BackupJob job, CancellationToken cancellationToken = default) =>
        storage.PrepareAsync(job, cancellationToken);

    public async Task<CompletedBackup> CompleteAsync(BackupJob job, string completedBackupPath, string stagingDirectory,
        DateTimeOffset completedUtc, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var stored = await new BackupDelivery(storage).DeliverAsync(job, completedBackupPath, stagingDirectory, cancellationToken);
            var record = await catalog.RegisterAsync(job, stored, completedUtc, cancellationToken);
            var retention = await new BackupRetention(catalog, storage).ApplyAsync(job, record, cancellationToken);
            return new CompletedBackup(record, retention);
        }
        finally { gate.Release(); }
    }
}
