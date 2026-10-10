namespace OneCBackup.Core;

public sealed record AgentConnectionResult(AgentSyncResult Synchronization, Exception? HeartbeatError);

/// <summary>Outgoing API sync and local scheduling run independently. The host supplies the backup executor.</summary>
public sealed class BackupAgent(AgentConfiguration configuration, string version)
{
    private readonly BackupScheduler scheduler = new();
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private string? lastConnectionSettings;
    private DateTimeOffset? nextConnection;

    public Task<IReadOnlyList<ScheduledExecution>> RunScheduledOnceAsync(AgentSettings settings, DateTimeOffset now,
        Func<BackupJob, CancellationToken, Task> execute, CancellationToken cancellationToken = default) =>
        RunScheduledCoreAsync(settings, now, execute, cancellationToken);

    private async Task<IReadOnlyList<ScheduledExecution>> RunScheduledCoreAsync(AgentSettings settings, DateTimeOffset now,
        Func<BackupJob, CancellationToken, Task> execute, CancellationToken cancellationToken)
    {
        var jobs = await configuration.GetEffectiveJobsAsync(settings, cancellationToken);
        return await scheduler.RunDueAsync(jobs, now, execute, cancellationToken);
    }

    public async Task<AgentConnectionResult?> ConnectOnceAsync(AgentSettings settings, DateTimeOffset now,
        bool backupExecutionAvailable = false, CancellationToken cancellationToken = default)
    {
        if (settings.Validate().Count != 0) throw new ArgumentException("Некорректные настройки агента.", nameof(settings));
        await connectionGate.WaitAsync(cancellationToken);
        try
        {
            if (settings.Mode == AgentMode.Standalone) { nextConnection = null; lastConnectionSettings = null; return null; }
            var fingerprint = $"{settings.AgentId}|{settings.NormalizedServerAddress}|{settings.CredentialKey}|{settings.SyncIntervalSeconds}";
            if (lastConnectionSettings == fingerprint && nextConnection > now) return null;
            lastConnectionSettings = fingerprint;
            nextConnection = now.AddSeconds(settings.SyncIntervalSeconds);
            var sync = await configuration.SynchronizeAsync(settings, now, cancellationToken);
            Exception? heartbeatError = null;
            try
            {
                var revision = await configuration.GetActiveRevisionAsync(settings, cancellationToken);
                await configuration.SendHeartbeatAsync(settings,
                    new(settings.AgentId, version, backupExecutionAvailable, revision, now.ToUniversalTime()), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { heartbeatError = ex; }
            return new(sync, heartbeatError);
        }
        finally { connectionGate.Release(); }
    }

    public async Task RunAsync(Func<CancellationToken, Task<AgentSettings>> loadSettings,
        Func<BackupJob, CancellationToken, Task> execute,
        Func<IReadOnlyList<ScheduledExecution>, Task> reportExecutions,
        Func<AgentConnectionResult, Task> reportConnection,
        TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        var clock = timeProvider ?? TimeProvider.System;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        async Task Participant(Func<CancellationToken, Task> run)
        {
            try { await run(stop.Token); }
            finally { await stop.CancelAsync(); }
        }
        async Task Network(CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var settings = await loadSettings(token);
                var result = await ConnectOnceAsync(settings, clock.GetUtcNow(), backupExecutionAvailable: true, cancellationToken: token);
                if (result is not null) await reportConnection(result);
                await Task.Delay(TimeSpan.FromSeconds(15), clock, token);
            }
        }
        await Task.WhenAll(
            Participant(token => scheduler.RunAsync(async ct =>
                await configuration.GetEffectiveJobsAsync(await loadSettings(ct), ct),
                execute, reportExecutions, timeProvider: clock, cancellationToken: token)),
            Participant(Network));
    }
}
