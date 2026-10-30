namespace SPBackup.Core;

public sealed record RetentionFailure(Guid BackupId, Exception Error);
public sealed record RetentionResult(IReadOnlyList<Guid> Deleted, IReadOnlyList<RetentionFailure> Failures)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class BackupRetention(BackupCatalog catalog, IBackupStorage storage)
{
    public async Task<RetentionResult> ApplyAsync(BackupJob job, BackupRecord newest,
        CancellationToken cancellationToken = default)
    {
        BackupProgressContext.Report(BackupStage.Retention);
        if (job.KeepLast < 1) throw new ArgumentException("Нужно сохранять хотя бы одну копию.", nameof(job));
        DiagnosticLog.Current.Write(DiagnosticEvent.RetentionStarted, jobId: job.Id, storageKind: job.StorageKind, values: new(Count: job.KeepLast));
        var identity = StorageAddress.Identity(job);
        var records = await catalog.ReadAsync(cancellationToken);
        if (newest.JobId != job.Id || newest.StorageIdentity != identity || !records.Contains(newest))
            throw new InvalidDataException("Очистка разрешена только после регистрации новой успешной копии этого задания.");
        await storage.VerifyAsync(newest.Backup, job, cancellationToken);
        // A partial source must never trigger deletion of earlier recoverable data.
        if (newest.Backup.FileSnapshot?.IsIncomplete == true)
            return new([], []) { Warnings = ["Очистка пропущена: новая копия неполная. Прежние копии сохранены."] };
        var owned = records.Where(r => r.JobId == job.Id && r.StorageIdentity == identity).ToArray();
        var keep = owned.Where(r => r.Id != newest.Id && r.Backup.FileSnapshot?.IsIncomplete != true).OrderByDescending(r => r.CompletedUtc).ThenByDescending(r => r.Id)
            .Take(job.KeepLast - 1).Select(r => r.Id).Append(newest.Id).ToHashSet();
        // Partial archives do not consume the successful-copy quota; removal is explicit.
        foreach (var partial in owned.Where(r => r.Backup.FileSnapshot?.IsIncomplete == true)) keep.Add(partial.Id);
        var deleted = new List<Guid>();
        var failures = new List<RetentionFailure>();
        // Keep the complete ancestry of every retained snapshot before deleting anything.
        foreach (var record in owned.Where(record => keep.Contains(record.Id) && record.Backup.FileSnapshot is not null).ToArray())
            foreach (var ancestor in FileBackupChains.Resolve(record, owned)) keep.Add(ancestor.Id);
        var partialCount = owned.Count(record => record.Backup.FileSnapshot?.IsIncomplete == true);
        var warnings = new List<string>();
        if (keep.Count - partialCount > job.KeepLast && owned.Any(record => record.Backup.FileSnapshot is not null))
            warnings.Add($"Для восстановления сохранено архивов: {keep.Count - partialCount}, лимит копий: {job.KeepLast}. Программа удалит старые архивы автоматически, когда они больше не понадобятся сохраняемым копиям.");
        if (partialCount > 0)
            warnings.Add($"Неполные копии сохранены вне лимита: {partialCount}. При необходимости удалите их вручную.");
        foreach (var record in owned.Where(r => !keep.Contains(r.Id)).OrderByDescending(r => r.Backup.FileSnapshot?.Depth ?? 0).ThenBy(r => r.CompletedUtc))
        {
            if (keep.Contains(record.Id)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await storage.DeleteAsync(record.Backup, job, cancellationToken);
                await catalog.RemoveAsync(record.Id, cancellationToken);
                deleted.Add(record.Id);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                DiagnosticLog.Current.Write(DiagnosticEvent.RetentionFailed, DiagnosticLevel.Warning, ex, jobId: job.Id, storageKind: job.StorageKind);
                failures.Add(new RetentionFailure(record.Id, ex));
                if (record.Backup.FileSnapshot is not null)
                    foreach (var ancestor in FileBackupChains.Resolve(record, owned)) keep.Add(ancestor.Id);
            }
        }
        DiagnosticLog.Current.Write(DiagnosticEvent.RetentionCompleted, jobId: job.Id, storageKind: job.StorageKind, values: new(Count: deleted.Count, Code: failures.Count));
        return new RetentionResult(deleted, failures) { Warnings = warnings };
    }
}

public sealed record CompletedBackup(BackupRecord Record, RetentionResult Retention);
public sealed record DestinationResult(string Destination, BackupRecord? Record, string? Error,
    IReadOnlyList<string>? Warnings = null);
public sealed record MultipleBackupCompletion(IReadOnlyList<DestinationResult> Destinations, bool Cancelled);

/// <summary>One coordinator per service: deliver, register success, then apply retention.</summary>
public sealed class BackupCompletion(IBackupStorage storage, BackupCatalog catalog, IArchivePasswordStore? passwords = null)
{
    internal BackupCatalog Catalog => catalog;
    internal IArchivePasswordStore? Passwords => passwords;
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
            DiagnosticLog.Current.Write(DiagnosticEvent.DestinationDeliveryStarted, jobId: job.Id, storageKind: job.StorageKind, destinationIndex: 1);
            var stored = await new BackupDelivery(storage, passwords).DeliverAsync(job, completedBackupPath, stagingDirectory, cancellationToken);
            DiagnosticLog.Current.Write(DiagnosticEvent.DestinationStored, jobId: job.Id, storageKind: job.StorageKind, destinationIndex: 1, values: new(Bytes: stored.Length));
            var record = await catalog.RegisterAsync(job, stored, completedUtc, cancellationToken);
            DiagnosticLog.Current.Write(DiagnosticEvent.DestinationRegistered, jobId: job.Id, storageKind: job.StorageKind, destinationIndex: 1);
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
            // Keep the prepared archive alive until every active delivery has finished cleanup.
            BackupProgressContext.Report(BackupStage.CopyingToStorage);
            using var deliveries = new SemaphoreSlim(3, 3);
            var tasks = job.StorageJobs.Select((target, index) => DeliverToAsync(target, index + 1)).ToArray();
            var outcomes = await Task.WhenAll(tasks);
            var results = outcomes.Select(outcome => outcome.Result).ToArray();
            return new MultipleBackupCompletion(results, outcomes.Any(outcome => outcome.Cancelled));

            async Task<(DestinationResult Result, bool Cancelled)> DeliverToAsync(BackupJob target, int index)
            {
                var destination = new BackupDestination { StorageKind = target.StorageKind,
                    DestinationDirectory = target.DestinationDirectory, Ftp = target.Ftp, GoogleDrive = target.GoogleDrive, KeepLast = target.KeepLast }.Description;
                using var progressDestination = BackupProgressContext.Destination(index);
                BackupProgressContext.Report(BackupStage.Waiting);
                BackupRecord? record = null;
                var acquired = false;
                try
                {
                    await deliveries.WaitAsync(cancellationToken);
                    acquired = true;
                    cancellationToken.ThrowIfCancellationRequested();
                    DiagnosticLog.Current.Write(DiagnosticEvent.DestinationDeliveryStarted, jobId: job.Id, storageKind: target.StorageKind, destinationIndex: index);
                    BackupProgressContext.Report(BackupStage.ConnectingStorage);
                    await storage.PrepareAsync(target, cancellationToken);
                    var stored = (await storage.StoreAsync(prepared.Path, target, cancellationToken))
                        with { ArchivePasswordKey = job.ArchivePasswordKey, FileSnapshot = prepared.FileSnapshot };
                    // A delivered file must enter the ledger even if cancellation was requested meanwhile.
                    DiagnosticLog.Current.Write(DiagnosticEvent.DestinationStored, jobId: job.Id, storageKind: target.StorageKind, destinationIndex: index, values: new(Bytes: stored.Length));
                    record = await catalog.RegisterAsync(target, stored, completedUtc, CancellationToken.None);
                    DiagnosticLog.Current.Write(DiagnosticEvent.DestinationRegistered, jobId: job.Id, storageKind: target.StorageKind, destinationIndex: index);
                    var retention = await new BackupRetention(catalog, storage).ApplyAsync(target, record, cancellationToken);
                    BackupProgressContext.Report(BackupStage.Completed);
                    return (new DestinationResult(destination, record, null,
                        retention.Failures.Select(f => "Очистка: " + f.Error.Message).Concat(retention.Warnings).ToArray()), false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    BackupProgressContext.Report(BackupStage.Cancelled);
                    return (new DestinationResult(destination, record, record is null ? "Доставка отменена или не начата." : null,
                        record is null ? null : ["Копия сохранена; очистка отменена."]), true);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Current.Write(DiagnosticEvent.DestinationFailed, DiagnosticLevel.Error, ex, jobId: job.Id, storageKind: target.StorageKind, destinationIndex: index);
                    BackupProgressContext.Report(BackupStage.Failed);
                    var error = ex is IOException && (target.StorageKind is StorageKind.LocalDirectory or StorageKind.NetworkShare) && File.Exists(target.DestinationDirectory)
                        ? "Вместо папки копий указан файл. Выберите другую папку в настройках хранилища." : ex.Message;
                    return (new DestinationResult(destination, record, record is null ? error : null,
                        record is null ? null : ["Копия сохранена; очистка: " + ex.Message]), false);
                }
                finally { if (acquired) deliveries.Release(); }
            }
        }
        finally { gate.Release(); }
    }
}
