namespace OneCBackup.Core;

public enum BackupRunStatus { Running, Succeeded, Failed, Cancelled, Interrupted, PartiallySucceeded }

public sealed record BackupRun(Guid RunId, Guid JobId, string JobName, DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc, BackupRunStatus Status, string WorkDirectory,
    StoredBackup? Backup = null, string? Error = null, IReadOnlyList<string>? Warnings = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DestinationResult>? Destinations { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusDescription => Status switch
    {
        BackupRunStatus.Running => "Выполняется", BackupRunStatus.Succeeded => "Успешно",
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

    private static void Validate(List<BackupRun> runs)
    {
        if (runs.Any(r => r is null || r.RunId == Guid.Empty || r.JobId == Guid.Empty ||
            !Enum.IsDefined(r.Status) || string.IsNullOrWhiteSpace(r.WorkDirectory) ||
            (r.Status == BackupRunStatus.Running) != (r.FinishedUtc is null)) ||
            runs.Select(r => r.RunId).Distinct().Count() != runs.Count)
            throw new InvalidDataException("История содержит некорректные или повторяющиеся запуски.");
    }
}
