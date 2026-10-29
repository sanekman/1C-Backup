namespace SPBackup.Core;

/// <summary>Read-only execution input; persistence and synchronization belong to the owner.</summary>
public interface IBackupJobSource
{
    Task<IReadOnlyList<BackupJob>> LoadAsync(CancellationToken cancellationToken = default);
}

public sealed record AgentJobSnapshot(AgentMode Mode, Guid AgentId, string? ServerAddress,
    long? Revision, IReadOnlyList<BackupJob> Jobs);

/// <summary>Bound for the lifetime of an executor. Changing mode or connection requires a new host.</summary>
public sealed class AgentExecutionJobSource(AgentConfiguration configuration, AgentSettings settings) : IBackupJobSource
{
    public async Task<IReadOnlyList<BackupJob>> LoadAsync(CancellationToken cancellationToken = default) =>
        (await configuration.GetSnapshotAsync(settings, cancellationToken)).Jobs;
}
