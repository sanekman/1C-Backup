namespace SPBackup.Core;

/// <summary>Checks a catalog entry against its owning job before accessing storage.</summary>
public sealed class BackupCopyVerifier(IBackupStorage storage)
{
    public async Task VerifyAsync(BackupRecord record, BackupJob job, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRecord(record, job);
        job = GetStorageJob(record, job);
        await storage.PrepareAsync(job, cancellationToken);
        DiagnosticLog.Current.Write(DiagnosticEvent.CopyVerificationStarted, jobId: job.Id, storageKind: job.StorageKind);
        try { await storage.VerifyAsync(record.Backup, job, cancellationToken);
            DiagnosticLog.Current.Write(DiagnosticEvent.CopyVerificationCompleted, jobId: job.Id, storageKind: job.StorageKind); }
        catch (Exception ex) { DiagnosticLog.Current.Write(DiagnosticEvent.CopyVerificationFailed, DiagnosticLevel.Error, ex, jobId: job.Id, storageKind: job.StorageKind); throw; }
    }

    internal static string ValidateRecord(BackupRecord record, BackupJob job)
    {
        if (record.JobId != job.Id)
            throw new InvalidDataException("Копия принадлежит другому заданию.");
        var errors = job.Validate();
        if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
        job = GetStorageJob(record, job);
        return StorageAddress.ValidateLocation(job, record.Backup);
    }

    public static BackupJob GetStorageJob(BackupRecord record, BackupJob job)
    {
        if (record.JobId != job.Id) throw new InvalidDataException("Копия принадлежит другому заданию.");
        var errors = job.Validate();
        if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
        return job.StorageJobs.FirstOrDefault(target => string.Equals(record.StorageIdentity,
            StorageAddress.Identity(target), StringComparison.Ordinal))
            ?? throw new InvalidDataException("Хранилище задания изменено. Для проверки этой копии нужны прежние настройки хранилища.");
    }
}
