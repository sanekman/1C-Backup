namespace OneCBackup.Core;

/// <summary>Checks a catalog entry against its owning job before accessing storage.</summary>
public sealed class BackupCopyVerifier(IBackupStorage storage)
{
    public async Task VerifyAsync(BackupRecord record, BackupJob job, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRecord(record, job);
        await storage.PrepareAsync(job, cancellationToken);
        await storage.VerifyAsync(record.Backup, job, cancellationToken);
    }

    internal static string ValidateRecord(BackupRecord record, BackupJob job)
    {
        if (record.JobId != job.Id)
            throw new InvalidDataException("Копия принадлежит другому заданию.");
        var errors = job.Validate();
        if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
        if (!string.Equals(record.StorageIdentity, StorageAddress.Identity(job), StringComparison.Ordinal))
            throw new InvalidDataException("Хранилище задания изменено. Для проверки этой копии нужны прежние настройки хранилища.");
        return StorageAddress.ValidateLocation(job, record.Backup);
    }
}
