namespace SPBackup.Core;

/// <summary>Checks every archive needed by a registered file restore point; other copies need only their own file.</summary>
public sealed class BackupPointVerifier(IBackupStorage storage, BackupCatalog catalog)
{
    public async Task<int> VerifyAsync(BackupRecord record, BackupJob job, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BackupCopyVerifier.GetStorageJob(record, job);
        var records = await catalog.ReadAsync(cancellationToken);
        if (!records.Contains(record)) throw new InvalidDataException("Выбранная копия больше не зарегистрирована.");
        IReadOnlyList<BackupRecord> chain = record.Backup.FileSnapshot is null ? [record] : FileBackupChains.Resolve(record, records);
        foreach (var archive in chain)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await new BackupCopyVerifier(storage).VerifyAsync(archive, job, cancellationToken); }
            catch (Exception ex) when (record.Backup.FileSnapshot is not null && ex is not OperationCanceledException)
            { throw new IOException("Не прошёл проверку архив файловой цепочки: " + archive.Backup.Location + ". " + ex.Message, ex); }
        }
        return chain.Count;
    }
}
