namespace OneCBackup.Core;

public sealed record StoredBackup(string Location, long Length, string Sha256)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ArchivePasswordKey { get; init; }
}

/// <summary>Stores a stable completed file; success means the stored bytes were verified.</summary>
public interface IBackupStorage
{
    /// <summary>Checks local prerequisites before database creation; does not prove remote access.</summary>
    Task PrepareAsync(BackupJob job, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task<StoredBackup> StoreAsync(string completedBackupPath, BackupJob job,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes only the exact recorded file in this job's destination.</summary>
    Task DeleteAsync(StoredBackup backup, BackupJob job, CancellationToken cancellationToken = default);
    Task VerifyAsync(StoredBackup backup, BackupJob job, CancellationToken cancellationToken = default);
}
