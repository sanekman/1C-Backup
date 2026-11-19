namespace SPBackup.Core;

public enum BackupRunStatus { Running, Succeeded, Failed, Cancelled, Interrupted, PartiallySucceeded }

public sealed record BackupRun(Guid RunId, Guid JobId, string JobName, DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc, BackupRunStatus Status, string WorkDirectory,
    StoredBackup? Backup = null, string? Error = null, IReadOnlyList<string>? Warnings = null)
{
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
    public string Details => Error ?? (Warnings?.Count > 0 ? string.Join(" ", Warnings) : Backup?.Location ?? "");
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
        }
        finally { gate.Release(); }
    }

    public async Task RecoverInterruptedAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var runs = await ReadCoreAsync(cancellationToken);
            if (!runs.Any(r => r.Status == BackupRunStatus.Running)) return;
            foreach (var interrupted in runs.Where(run => run.Status == BackupRunStatus.Running))
                DiagnosticLog.Current.Write(DiagnosticEvent.RunInterrupted, DiagnosticLevel.Warning, jobId: interrupted.JobId, runId: interrupted.RunId);
            runs = runs.Select(r => r.Status == BackupRunStatus.Running
                ? r with { Status = BackupRunStatus.Interrupted, FinishedUtc = DateTimeOffset.UtcNow,
                    Issues = [new("Interrupted", "Error", "Backup")],
                    Error = "Предыдущее исполнение не завершилось. Проверьте рабочий каталог и журнал платформы." } : r).ToList();
            await file.WriteAsync(runs, cancellationToken);
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
