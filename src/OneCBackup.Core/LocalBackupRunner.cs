namespace OneCBackup.Core;

/// <summary>Serializes runs and records failures before returning them to the desktop or service.</summary>
public sealed class LocalBackupRunner(IBackupExecutor executor, BackupCompletion completion,
    BackupRunHistory history, string workRoot)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<BackupRun> RunAsync(BackupJob job, CancellationToken cancellationToken = default)
    {
        var errors = job.Validate();
        if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var id = Guid.NewGuid();
            var work = Path.Combine(Path.GetFullPath(workRoot), id.ToString("N"));
            var run = new BackupRun(id, job.Id, job.Name, DateTimeOffset.UtcNow, null, BackupRunStatus.Running, work);
            // A corrupt or unwritable history prevents starting an untracked operation.
            await history.SaveAsync(run, cancellationToken);
            try
            {
                Directory.CreateDirectory(work);
                await completion.PrepareAsync(job, cancellationToken);
                var dump = await executor.CreateAsync(job, work, cancellationToken);
                var completed = await completion.CompleteAsync(job, dump, Path.Combine(work, "archive"),
                    DateTimeOffset.UtcNow, cancellationToken);
                var warnings = completed.Retention.Failures.Select(f => "Очистка: " + f.Error.Message).ToList();
                // Own working files are removed only after verified delivery and registration.
                try { Directory.Delete(work, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { warnings.Add("Не удалось удалить рабочие файлы: " + ex.Message); }
                run = run with { Status = BackupRunStatus.Succeeded, Backup = completed.Record.Backup, Warnings = warnings };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { run = run with { Status = BackupRunStatus.Cancelled, Error = "Операция отменена. Рабочие файлы сохранены." }; }
            catch (Exception ex)
            { run = run with { Status = BackupRunStatus.Failed, Error = ex.Message }; }
            run = run with { FinishedUtc = DateTimeOffset.UtcNow };
            // Cancellation must not discard the terminal report.
            await history.SaveAsync(run, CancellationToken.None);
            return run;
        }
        finally { gate.Release(); }
    }
}
