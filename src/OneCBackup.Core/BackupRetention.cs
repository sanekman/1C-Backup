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
public sealed record DestinationResult(string Destination, BackupRecord? Record, string? Error,
    IReadOnlyList<string>? Warnings = null);
public sealed record MultipleBackupCompletion(IReadOnlyList<DestinationResult> Destinations, bool Cancelled);

/// <summary>One coordinator per service: deliver, register success, then apply retention.</summary>
public sealed class BackupCompletion(IBackupStorage storage, BackupCatalog catalog, IArchivePasswordStore? passwords = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task PrepareAsync(BackupJob job, CancellationToken cancellationToken = default)
    {
        // Refuse before creating a plaintext dump when the password is unavailable.
        await ArchivePasswords.ResolveAsync(job.ArchivePasswordKey, passwords, cancellationToken);
        // A missing FTP login must not prevent delivery to other independent destinations.
        if (job.AdditionalDestinations?.Count is not > 0) await storage.PrepareAsync(job, cancellationToken);
    }

    public async Task<CompletedBackup> CompleteAsync(BackupJob job, string completedBackupPath, string stagingDirectory,
        DateTimeOffset completedUtc, CancellationToken cancellationToken = default)
    {
        if (job.AdditionalDestinations?.Count > 0)
            throw new InvalidOperationException("Для нескольких хранилищ используйте CompleteManyAsync.");
        await gate.WaitAsync(cancellationToken);
        try
        {
            var stored = await new BackupDelivery(storage, passwords).DeliverAsync(job, completedBackupPath, stagingDirectory, cancellationToken);
            var record = await catalog.RegisterAsync(job, stored, completedUtc, cancellationToken);
            var retention = await new BackupRetention(catalog, storage).ApplyAsync(job, record, cancellationToken);
            return new CompletedBackup(record, retention);
        }
        finally { gate.Release(); }
    }

    public async Task<MultipleBackupCompletion> CompleteManyAsync(BackupJob job, string completedBackupPath,
        string stagingDirectory, DateTimeOffset completedUtc, CancellationToken cancellationToken = default)
    {
        var errors = job.Validate();
        if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var prepared = await new BackupDelivery(storage, passwords).PrepareFileAsync(job,
                completedBackupPath, stagingDirectory, cancellationToken);
            var results = new List<DestinationResult>();
            var cancelled = false;
            foreach (var target in job.StorageJobs)
            {
                var destination = new BackupDestination { StorageKind = target.StorageKind,
                    DestinationDirectory = target.DestinationDirectory, Ftp = target.Ftp, KeepLast = target.KeepLast }.Description;
                BackupRecord? record = null;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await storage.PrepareAsync(target, cancellationToken);
                    var stored = (await storage.StoreAsync(prepared.Path, target, cancellationToken))
                        with { ArchivePasswordKey = job.ArchivePasswordKey };
                    // A delivered file must enter the ledger even if cancellation was requested meanwhile.
                    record = await catalog.RegisterAsync(target, stored, completedUtc, CancellationToken.None);
                    var retention = await new BackupRetention(catalog, storage).ApplyAsync(target, record, cancellationToken);
                    results.Add(new DestinationResult(destination, record, null,
                        retention.Failures.Select(f => "Очистка: " + f.Error.Message).ToArray()));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    results.Add(new DestinationResult(destination, record, record is null ? "Доставка отменена или не начата." : null,
                        record is null ? null : ["Копия сохранена; очистка отменена."]));
                }
                catch (Exception ex)
                {
                    var error = ex is IOException && target.StorageKind != StorageKind.Ftp && File.Exists(target.DestinationDirectory)
                        ? "Вместо папки копий указан файл. Выберите другую папку в настройках хранилища." : ex.Message;
                    results.Add(new DestinationResult(destination, record, record is null ? error : null,
                        record is null ? null : ["Копия сохранена; очистка: " + ex.Message]));
                }
            }
            return new MultipleBackupCompletion(results, cancelled ||
                cancellationToken.IsCancellationRequested && results.Any(result => result.Error is not null));
        }
        finally { gate.Release(); }
    }
}
