namespace SPBackup.Core;

public enum BackupRunStatus { Running, Succeeded, Failed, Cancelled, Interrupted, PartiallySucceeded }

public sealed record BackupRun(Guid RunId, Guid JobId, string JobName, DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc, BackupRunStatus Status, string WorkDirectory,
    StoredBackup? Backup = null, string? Error = null, IReadOnlyList<string>? Warnings = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public long? DurationMilliseconds { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public BackupConfigurationStamp? Configuration { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Monitoring.MonitoredIssue>? Issues { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DestinationResult>? Destinations { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusDescription => Status switch
    {
        BackupRunStatus.Running => "Выполняется", BackupRunStatus.Succeeded => Warnings?.Count > 0 ? "С предупреждениями" : "Успешно",
        BackupRunStatus.Cancelled => "Отменено", BackupRunStatus.Interrupted => "Прервано",
        BackupRunStatus.PartiallySucceeded => "Частично", _ => "Ошибка"
    };
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Comment => Issues?.Any(issue => issue is { Code: "OneCOpenDatabase", Severity: "Info" }) == true
        ? OneCFileArchiveExecutor.OpenDatabaseComment : null;
    [System.Text.Json.Serialization.JsonIgnore]
    public string Details => Error ?? (Warnings?.Count > 0 ? string.Join(" ", Warnings) : Comment ?? Backup?.Location ?? "");
}

/// <summary>One history instance and one executor host per local data directory.</summary>
public sealed class BackupRunHistory(string path)
{
    private readonly AgentJsonFile<List<BackupRun>> file = new(path);
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<IReadOnlyList<BackupRun>> ReadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { return await ReadCoreAsync(cancellationToken); }
        finally { gate.Release(); }
    }

    internal async Task<IReadOnlyList<BackupRun>?> ReadRecentForDiagnosticsAsync(int count, long maximumBytes, CancellationToken token)
    {
        await using var stream = await AtomicProfileFile.OpenReadAsync(path, token);
        if (stream is null) return [];
        AgentJsonFile<List<BackupRun>>.ValidateReadPath(path, stream);
        if (stream.Length > maximumBytes) return null;
        var recent = new PriorityQueue<BackupRun, (DateTimeOffset Time, int Order)>();
        var ids = new HashSet<Guid>();
        await foreach (var run in System.Text.Json.JsonSerializer.DeserializeAsyncEnumerable<BackupRun>(stream,
            AgentJsonFile<List<BackupRun>>.Options, token))
        {
            if (!ValidRun(run) || !ids.Add(run!.RunId)) throw new InvalidDataException("История содержит некорректные или повторяющиеся запуски.");
            var priority = (run.StartedUtc, -ids.Count);
            if (recent.Count < count) recent.Enqueue(run, priority);
            else recent.EnqueueDequeue(run, priority);
        }
        return recent.UnorderedItems.OrderByDescending(item => item.Priority).Select(item => item.Element).ToArray();
    }

    public async Task SaveAsync(BackupRun run, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var runs = await ReadCoreAsync(cancellationToken);
            runs.RemoveAll(r => r.RunId == run.RunId);
            runs.Add(run);
            Validate(runs);
            await file.WriteAsync(runs, cancellationToken);
            if (run.FinishedUtc.HasValue) await BackupNotificationQueue.CaptureAsync(path, [run], cancellationToken);
        }
        finally { gate.Release(); }
    }

    public Task RecoverInterruptedAsync(CancellationToken cancellationToken = default) =>
        RecoverInterruptedCoreAsync(null, cancellationToken);

    public Task RecoverInterruptedDeliveriesAsync(IEnumerable<BackupJob> jobs, string workRoot, BackupCatalog catalog,
        IBackupStorage storage, CancellationToken cancellationToken = default)
    {
        var configured = jobs.ToDictionary(job => job.Id);
        return RecoverInterruptedCoreAsync((run, token) =>
            configured.TryGetValue(run.JobId, out var job) && job.ExecutionConfiguration == run.Configuration
                ? BackupDeliveryJournal.ReconcileAsync(run, job, workRoot, catalog, storage, token)
                : Task.FromResult<IReadOnlyList<DestinationResult>>([]), cancellationToken);
    }

    internal Task RecoverInterruptedDeliveriesAsync(IBackupJobSource source, string workRoot, BackupCatalog catalog,
        IBackupStorage storage, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Guid, BackupJob>? configured = null;
        return RecoverInterruptedCoreAsync(async (run, token) =>
        {
            if (configured is null)
            {
                try { configured = (await source.LoadAsync(token)).ToDictionary(job => job.Id); }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    DiagnosticLog.Current.Write(DiagnosticEvent.AgentWarning, DiagnosticLevel.Warning, error);
                    configured = new Dictionary<Guid, BackupJob>();
                }
            }
            return configured.TryGetValue(run.JobId, out var job) && job.ExecutionConfiguration == run.Configuration
                ? await BackupDeliveryJournal.ReconcileAsync(run, job, workRoot, catalog, storage, token) : [];
        }, cancellationToken);
    }

    private async Task RecoverInterruptedCoreAsync(Func<BackupRun, CancellationToken, Task<IReadOnlyList<DestinationResult>>>? reconcile,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var runs = await ReadCoreAsync(cancellationToken);
            if (!runs.Any(run => run.Status == BackupRunStatus.Running)) return;
            var interrupted = new List<BackupRun>();
            for (var index = 0; index < runs.Count; index++)
            {
                var run = runs[index];
                if (run.Status != BackupRunStatus.Running) continue;
                DiagnosticLog.Current.Write(DiagnosticEvent.RunInterrupted, DiagnosticLevel.Warning, jobId: run.JobId, runId: run.RunId);
                var recovered = reconcile is null ? [] : await reconcile(run, cancellationToken);
                var finished = DateTimeOffset.UtcNow;
                runs[index] = run with
                {
                    Status = BackupRunStatus.Interrupted,
                    FinishedUtc = finished < run.StartedUtc ? run.StartedUtc : finished,
                    Issues = [new("Interrupted", "Error", "Backup")],
                    Error = "Предыдущее исполнение не завершилось. Проверьте рабочий каталог и результаты по хранилищам.",
                    Destinations = recovered.Count == 0 ? run.Destinations : recovered,
                    Backup = recovered.FirstOrDefault(item => item.Record is not null)?.Record?.Backup ?? run.Backup
                };
                interrupted.Add(runs[index]);
            }
            await file.WriteAsync(runs, cancellationToken);
            await BackupNotificationQueue.CaptureAsync(path, interrupted, cancellationToken);
        }
        finally { gate.Release(); }
    }

    private async Task<List<BackupRun>> ReadCoreAsync(CancellationToken cancellationToken)
    {
        var runs = await file.ReadAsync(cancellationToken) ?? [];
        Validate(runs);
        return runs;
    }

    private static bool ValidRun(BackupRun? run) => run is not null && run.RunId != Guid.Empty && run.JobId != Guid.Empty
        && run.Configuration is not { Valid: false } && Enum.IsDefined(run.Status) && !string.IsNullOrWhiteSpace(run.WorkDirectory)
        && (run.Status == BackupRunStatus.Running) == (run.FinishedUtc is null);

    private static void Validate(List<BackupRun> runs)
    {
        if (runs.Any(run => !ValidRun(run)) || runs.Select(run => run.RunId).Distinct().Count() != runs.Count)
            throw new InvalidDataException("История содержит некорректные или повторяющиеся запуски.");
    }
}
