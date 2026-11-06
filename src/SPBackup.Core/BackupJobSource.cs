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
    internal string? ConnectionId => settings.Mode == AgentMode.Managed ? ManagedAgentProfile.Identity(settings) : null;
    public async Task<IReadOnlyList<BackupJob>> LoadAsync(CancellationToken cancellationToken = default) =>
        (await LoadSnapshotAsync(cancellationToken)).Jobs;

    public async Task<AgentJobSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await configuration.GetSnapshotAsync(settings, cancellationToken);
        if (snapshot.Mode == AgentMode.Standalone || snapshot.Revision is null) return snapshot;
        var stamp = new BackupConfigurationStamp(ManagedAgentProfile.Identity(settings), snapshot.Revision.Value);
        return snapshot with { Jobs = Array.AsReadOnly(snapshot.Jobs.Select(job => job with { ExecutionConfiguration = stamp }).ToArray()) };
    }
}
