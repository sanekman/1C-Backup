namespace OneCBackup.Core;

/// <summary>Shares one execution gate between manual runs and a restartable local schedule loop.</summary>
public sealed class LocalBackupDispatcher(JobStore store, Func<BackupJob, CancellationToken, Task> execute)
{
    private readonly BackupScheduler scheduler = new(new StartupBackupSchedule(store.StartupSchedulePath));
    private readonly SemaphoreSlim executionGate = new(1, 1);
    private int scheduling;

    public static bool Supports(BackupJob job) => Enum.IsDefined(job.Kind) &&
        Enum.IsDefined(job.StorageKind);

    public async Task ExecuteAsync(BackupJob job, CancellationToken cancellationToken = default)
    {
        if (!Supports(job)) throw new NotSupportedException("Неизвестный тип базы или хранилища.");
        await executionGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await execute(job, cancellationToken);
        }
        finally { executionGate.Release(); }
    }

    public async Task<IReadOnlyList<BackupJob>> LoadScheduledJobsAsync(CancellationToken cancellationToken = default)
    {
        var jobs = await store.LoadAsync(cancellationToken);
        var unsupported = jobs.Where(j => j.Enabled && j.ScheduleKind != ScheduleKind.Manual && !Supports(j)).ToArray();
        if (unsupported.Length != 0)
            throw new NotSupportedException("Планировщик не может исполнять эти задания: " +
                string.Join(", ", unsupported.Select(j => j.Name)) + ". Отключите их или выберите запуск только вручную.");
        return jobs;
    }

    public async Task RunScheduleAsync(Func<IReadOnlyList<ScheduledExecution>, Task> report,
        CancellationToken cancellationToken = default, TimeSpan? pollInterval = null, TimeProvider? timeProvider = null)
    {
        if (Interlocked.CompareExchange(ref scheduling, 1, 0) != 0)
            throw new InvalidOperationException("Планировщик уже запущен.");
        try
        {
            // Retain this scheduler across stop/start so an already consumed slot is not replayed.
            await scheduler.RunAsync(LoadScheduledJobsAsync, ExecuteAsync, report,
                pollInterval, timeProvider, cancellationToken);
        }
        finally { Interlocked.Exchange(ref scheduling, 0); }
    }
}
