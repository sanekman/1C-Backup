namespace OneCBackup.Core;

public sealed record ExternalBackupPollResult(Guid MonitorId, string MonitorName, DateTimeOffset PolledUtc,
    int Observations, int Added, string? Error = null);

public sealed class ExternalBackupPollingStatusStore(string directory)
{
    private readonly AgentJsonFile<List<ExternalBackupPollResult>> file = new(Path.Combine(directory, "external-poll-status.json"));
    public async Task<IReadOnlyList<ExternalBackupPollResult>> ReadAsync(CancellationToken cancellationToken = default)
    {
        var values = await file.ReadAsync(cancellationToken) ?? [];
        if (values.Any(value => value is null || value.MonitorId == Guid.Empty || string.IsNullOrWhiteSpace(value.MonitorName) ||
            value.PolledUtc == default || value.Observations < 0 || value.Added < 0) ||
            values.Select(value => value.MonitorId).Distinct().Count() != values.Count)
            throw new InvalidDataException("Некорректный журнал опроса источников.");
        return values;
    }
    internal Task WriteAsync(IReadOnlyList<ExternalBackupPollResult> values, CancellationToken cancellationToken) =>
        file.WriteAsync(values.ToList(), cancellationToken);
}

/// <summary>One loop per engine owner. A failed source does not prevent collecting other sources.</summary>
public sealed class ExternalBackupPoller(ExternalBackupMonitorStore store, ExternalBackupOutbox outbox,
    AgentSettings settings, ExternalBackupPollingStatusStore status,
    Func<ExternalBackupMonitor, IExternalBackupSource>? sourceFactory = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<Guid, (ExternalBackupMonitor Settings, DateTimeOffset Next)> pending = [];

    public async Task<IReadOnlyList<ExternalBackupPollResult>> PollAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var monitors = await store.LoadAsync(cancellationToken);
            foreach (var removed in pending.Keys.Except(monitors.Where(m => m.Enabled).Select(m => m.Id)).ToArray()) pending.Remove(removed);
            var results = new List<ExternalBackupPollResult>();
            foreach (var monitor in monitors.Where(m => m.Enabled))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pending.TryGetValue(monitor.Id, out var next) && next.Settings == monitor && next.Next > now) continue;
                pending[monitor.Id] = (monitor, now.AddSeconds(monitor.PollIntervalSeconds));
                try
                {
                    var source = sourceFactory?.Invoke(monitor) ?? (monitor.Kind == ExternalBackupSourceKind.SqlServerHistory
                        ? (IExternalBackupSource)new SqlServerBackupHistorySource() : new PostgreSqlBackupReportSource());
                    var observations = await source.ReadAsync(monitor, cancellationToken);
                    var added = await outbox.ObserveAsync(settings, monitor, observations, cancellationToken);
                    results.Add(new ExternalBackupPollResult(monitor.Id, monitor.Name, now.ToUniversalTime(), observations.Count, added));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { results.Add(new ExternalBackupPollResult(monitor.Id, monitor.Name, now.ToUniversalTime(), 0, 0, ex.Message)); }
            }
            var old = await status.ReadAsync(cancellationToken);
            var currentIds = monitors.Select(m => m.Id).ToHashSet();
            if (results.Count != 0 || old.Any(value => !currentIds.Contains(value.MonitorId)))
            {
                await status.WriteAsync(old.Where(value => currentIds.Contains(value.MonitorId) && !results.Any(r => r.MonitorId == value.MonitorId))
                    .Concat(results).ToList(), cancellationToken);
            }
            return results;
        }
        finally { gate.Release(); }
    }
}
