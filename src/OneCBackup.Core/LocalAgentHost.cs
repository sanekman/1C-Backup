namespace OneCBackup.Core;

/// <summary>Standalone, current-user host. Owns execution until all operations and reports have stopped.</summary>
public sealed class LocalAgentHost(string directory, IBackupExecutor executor, IFtpCredentialStore credentials)
{
    public async Task<int> RunAsync(Guid? runJobId = null, TimeSpan? pollInterval = null,
        Func<BackupRun, Task>? report = null, CancellationToken cancellationToken = default)
    {
        using var lease = new BackupEngineCoordinator(directory).Acquire();
        var settings = await new AgentSettingsStore(Path.Combine(directory, "agent.json")).LoadAsync(cancellationToken);
        if (settings.Mode != AgentMode.Standalone)
            throw new NotSupportedException("Локальный фоновый агент пока поддерживает только автономный режим.");
        var store = new JobStore(Path.Combine(directory, "jobs.json"));
        var jobs = await store.LoadAsync(cancellationToken);
        var manual = runJobId is Guid id ? jobs.SingleOrDefault(job => job.Id == id)
            ?? throw new InvalidDataException("Задание не найдено.") : null;
        var history = new BackupRunHistory(Path.Combine(directory, "runs.json"));
        await history.RecoverInterruptedAsync(cancellationToken);
        var storage = new RoutedBackupStorage(credentials, Path.Combine(directory, "ftp-verification"));
        var catalog = new BackupCatalog(Path.Combine(directory, "copies.json"));
        var runner = new LocalBackupRunner(executor, new BackupCompletion(storage, catalog), history, Path.Combine(directory, "work"));
        var control = new LocalAgentControlStore(directory);
        var now = DateTimeOffset.UtcNow;
        var state = new LocalAgentState(Guid.NewGuid(), Environment.ProcessId, now, now, LocalAgentPhase.Running);
        await control.WriteAsync(state, cancellationToken);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        string? lastError = null;
        string? monitorError = null;
        BackupRun? lastRun = null;
        var dispatcher = new LocalBackupDispatcher(store, async (job, token) =>
        {
            lastRun = await runner.RunAsync(job, token);
            Volatile.Write(ref lastError, lastRun.Status is BackupRunStatus.Failed or BackupRunStatus.PartiallySucceeded ? lastRun.Error : null);
            if (report is not null) await report(lastRun);
        });

        async Task ControlLoop()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                if (await control.StopRequestedAsync(state.SessionId, lifetime.Token))
                {
                    await control.WriteAsync(state with { Phase = LocalAgentPhase.Stopping, UpdatedUtc = DateTimeOffset.UtcNow }, CancellationToken.None);
                    lifetime.Cancel();
                    return;
                }
                await control.WriteAsync(state with { UpdatedUtc = DateTimeOffset.UtcNow, Error = Volatile.Read(ref lastError),
                    MonitoringError = Volatile.Read(ref monitorError) }, lifetime.Token);
            }
        }

        var controlTask = ControlLoop();
        async Task MonitorLoop()
        {
            var monitorStore = new ExternalBackupMonitorStore(Path.Combine(directory, "external-monitors.json"));
            var poller = new ExternalBackupPoller(monitorStore,
                new ExternalBackupOutbox(Path.Combine(directory, "external-backups.json")), settings,
                new ExternalBackupPollingStatusStore(directory));
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            var failures = new Dictionary<Guid, string>();
            do
            {
                try
                {
                    var results = await poller.PollAsync(DateTimeOffset.UtcNow, lifetime.Token);
                    var enabledIds = (await monitorStore.LoadAsync(lifetime.Token)).Where(m => m.Enabled).Select(m => m.Id);
                    foreach (var removed in failures.Keys.Except(enabledIds).ToArray()) failures.Remove(removed);
                    foreach (var result in results)
                        if (result.Error is null) failures.Remove(result.MonitorId);
                        else failures[result.MonitorId] = result.MonitorName + ": " + result.Error;
                    Volatile.Write(ref monitorError, failures.Count == 0 ? null : string.Join("; ", failures.Values));
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                catch (Exception ex) { Volatile.Write(ref monitorError, "Мониторинг SQL: " + ex.Message); }
            } while (await timer.WaitForNextTickAsync(lifetime.Token));
        }
        var executionTask = manual is null
            ? dispatcher.RunScheduleAsync(_ => Task.CompletedTask, lifetime.Token, pollInterval)
            : dispatcher.ExecuteAsync(manual, lifetime.Token);
        var monitoringTask = manual is null ? MonitorLoop() : Task.CompletedTask;
        Exception? failure = null;
        try
        {
            var completed = manual is null
                ? await Task.WhenAny(executionTask, controlTask, monitoringTask)
                : await Task.WhenAny(executionTask, controlTask);
            await completed;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; }
        finally
        {
            lifetime.Cancel();
            try { await Task.WhenAll(executionTask, controlTask, monitoringTask); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception ex) { failure ??= ex; }
            var error = failure?.Message ?? (manual is not null && (lastRun?.Status is BackupRunStatus.Failed or BackupRunStatus.PartiallySucceeded) ? lastRun.Error : null);
            await control.WriteAsync(state with { UpdatedUtc = DateTimeOffset.UtcNow,
                Phase = error is null ? LocalAgentPhase.Stopped : LocalAgentPhase.Faulted, Error = error,
                MonitoringError = Volatile.Read(ref monitorError) }, CancellationToken.None);
        }
        return failure is not null || manual is not null && (lastRun?.Status is BackupRunStatus.Failed or BackupRunStatus.PartiallySucceeded) ? 1 : 0;
    }
}
