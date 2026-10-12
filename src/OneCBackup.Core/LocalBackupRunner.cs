namespace OneCBackup.Core;

/// <summary>Serializes runs and records failures before returning them to the desktop or service.</summary>
public sealed class LocalBackupRunner(IBackupExecutor executor, BackupCompletion completion,
    BackupRunHistory history, string workRoot, Func<BackupJob, WebDavClient>? webDavClientFactory = null, DiagnosticLog? diagnosticLog = null, Action<BackupProgressSnapshot>? progress = null)
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
            using var progressScope = BackupProgressContext.Begin(job, id, progress);
            var log = diagnosticLog ?? DiagnosticLog.Current;
            using var diagnosticScope = DiagnosticLog.Use(log, job.Id, id, job.Kind);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            log.Write(DiagnosticEvent.RunStarted, values: new(Count: job.StorageJobs.Count));
            var work = Path.Combine(Path.GetFullPath(job.TemporaryDirectory ?? workRoot), id.ToString("N"));
            var run = new BackupRun(id, job.Id, job.Name, DateTimeOffset.UtcNow, null, BackupRunStatus.Running, work);
            // A corrupt or unwritable history prevents starting an untracked operation.
            try { await history.SaveAsync(run, cancellationToken); }
            catch (Exception ex) { log.Write(DiagnosticEvent.RunFailed, DiagnosticLevel.Error, ex); throw; }
            try
            {
                Directory.CreateDirectory(work);
                log.Write(DiagnosticEvent.SourcePreparationStarted);
                BackupProgressContext.Report(BackupStage.PreparingStorage);
                await completion.PrepareAsync(job, cancellationToken);
                log.Write(DiagnosticEvent.SourcePreparationCompleted);
                var fileSnapshots = new FileBackupSnapshotStore(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(workRoot))!, "file-snapshots"));
                IBackupExecutor selectedExecutor = job.Kind switch
                {
                    DatabaseKind.FilesAndFolders => new FileFolderBackupExecutor(completion.Catalog, fileSnapshots, completion.Passwords),
                    DatabaseKind.WebDav => new WebDavBackupExecutor(completion.Catalog, fileSnapshots, completion.Passwords, webDavClientFactory),
                    _ => executor
                };
                log.Write(DiagnosticEvent.BackupCreationStarted);
                BackupProgressContext.Report(BackupStage.ConnectingSource);
                BackupProgressContext.Report(BackupStage.CreatingBackup);
                var dump = await selectedExecutor.CreateAsync(job, work, cancellationToken);
                log.Write(DiagnosticEvent.BackupCreationCompleted, values: new(Bytes: new FileInfo(dump).Length));
                FileBackupManifest? fileManifest = null;
                if (job.IsFileSnapshot)
                    fileManifest = await FileBackupZip.ReadManifestAsync(dump,
                        await ArchivePasswords.ResolveAsync(job.ArchivePasswordKey, completion.Passwords, cancellationToken), cancellationToken);
                log.Write(DiagnosticEvent.BackupDeliveryStarted);
                var warnings = new List<string>();
                if (job.AdditionalDestinations?.Count > 0)
                {
                    var completed = await completion.CompleteManyAsync(job, dump, Path.Combine(work, "archive"),
                        DateTimeOffset.UtcNow, cancellationToken);
                    var copies = completed.Destinations.Where(result => result.Record is not null).ToArray();
                    var failures = completed.Destinations.Where(result => result.Error is not null).ToArray();
                    warnings.AddRange(completed.Destinations.SelectMany(result => (result.Warnings ?? [])
                        .Select(warning => result.Destination + ": " + warning)));
                    run = run with
                    {
                        Status = completed.Cancelled ? BackupRunStatus.Cancelled : failures.Length == 0 ? BackupRunStatus.Succeeded
                            : copies.Length == 0 ? BackupRunStatus.Failed : BackupRunStatus.PartiallySucceeded,
                        Backup = copies.FirstOrDefault()?.Record?.Backup, Destinations = completed.Destinations,
                        Error = completed.Cancelled ? "Операция отменена. Доставленные копии сохранены; рабочие файлы оставлены."
                            : failures.Length == 0 ? null : string.Join("\n", failures.Select(result => result.Destination + ": " + result.Error))
                    };
                }
                else
                {
                    var completed = await completion.CompleteAsync(job, dump, Path.Combine(work, "archive"),
                        DateTimeOffset.UtcNow, cancellationToken);
                    warnings.AddRange(completed.Retention.Failures.Select(f => "Очистка: " + f.Error.Message));
                    warnings.AddRange(completed.Retention.Warnings);
                    run = run with { Status = BackupRunStatus.Succeeded, Backup = completed.Record.Backup };
                }
                if (fileManifest is not null && run.Backup is not null)
                {
                    try { await fileSnapshots.SaveAsync(job.Id, fileManifest, CancellationToken.None); }
                    catch (Exception ex) { log.Write(DiagnosticEvent.SnapshotCacheFailed, DiagnosticLevel.Warning, ex); warnings.Add("Индекс инкрементной копии не сохранён; следующая копия будет полной: " + ex.Message); }
                }
                // Own working files are removed only after verified delivery and registration.
                if (run.Status == BackupRunStatus.Succeeded)
                {
                    try { Directory.Delete(work, recursive: true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { log.Write(DiagnosticEvent.WorkCleanupFailed, DiagnosticLevel.Warning, ex); warnings.Add("Не удалось удалить рабочие файлы: " + ex.Message); }
                }
                run = run with { Warnings = warnings };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { log.Write(DiagnosticEvent.RunCancelled); run = run with { Status = BackupRunStatus.Cancelled, Error = "Операция отменена. Рабочие файлы сохранены." }; }
            catch (Exception ex)
            { log.Write(DiagnosticEvent.RunFailed, DiagnosticLevel.Error, ex); run = run with { Status = BackupRunStatus.Failed, Error = ex.Message }; }
            run = run with { FinishedUtc = DateTimeOffset.UtcNow };
            // Cancellation must not discard the terminal report.
            try { await history.SaveAsync(run, CancellationToken.None); }
            catch (Exception ex) { log.Write(DiagnosticEvent.RunFailed, DiagnosticLevel.Error, ex); throw; }
            log.Write(DiagnosticEvent.RunFinished, run.Status is BackupRunStatus.Failed or BackupRunStatus.PartiallySucceeded ? DiagnosticLevel.Error : DiagnosticLevel.Info,
                outcome: run.Status, values: new(Count: run.Warnings?.Count ?? 0, Bytes: run.Backup?.Length, Milliseconds: clock.ElapsedMilliseconds));
            BackupProgressContext.Report(run.Status switch { BackupRunStatus.Succeeded => BackupStage.Completed, BackupRunStatus.Cancelled => BackupStage.Cancelled, BackupRunStatus.PartiallySucceeded => BackupStage.PartiallySucceeded, _ => BackupStage.Failed });
            return run;
        }
        finally { gate.Release(); }
    }
}
