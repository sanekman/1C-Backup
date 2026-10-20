namespace OneCBackup.Core;

/// <summary>Standalone, current-user host. Owns execution until all operations and reports have stopped.</summary>
public sealed class LocalAgentHost(string directory, IBackupExecutor executor, IFtpCredentialStore credentials)
{
    public async Task<int> RunAsync(Guid? runJobId = null, TimeSpan? pollInterval = null,
        Func<BackupRun, Task>? report = null, CancellationToken cancellationToken = default)
    {
        DiagnosticLog.Current.Write(DiagnosticEvent.AgentStarted);
        using var lease = new BackupEngineCoordinator(directory).Acquire();
        var warnings = new AgentRuntimeWarnings();
        AgentSettings? settings = null;
        try { settings = await new AgentSettingsStore(Path.Combine(directory, "agent.json")).LoadAsync(cancellationToken); }
        catch (Exception error) when (runJobId is null && AgentRuntimeWarnings.Recoverable(error)) { warnings.Set("Настройки агента", error); }
        if (settings is not null && settings.Mode != AgentMode.Standalone)
            throw new NotSupportedException("Локальный фоновый агент пока поддерживает только автономный режим.");
        var store = new JobStore(Path.Combine(directory, "jobs.json"), useLocalTime: true);
        var jobs = runJobId is not null ? await store.LoadAsync(cancellationToken) : [];
        var manual = runJobId is Guid id ? jobs.SingleOrDefault(job => job.Id == id)
            ?? throw new InvalidDataException("Задание не найдено.") : null;
        var history = new BackupRunHistory(Path.Combine(directory, "runs.json"));
        var storage = new RoutedBackupStorage(credentials, Path.Combine(directory, "ftp-verification"));
        var catalog = new BackupCatalog(Path.Combine(directory, "copies.json"));
        var progressStore = new BackupProgressStore(directory);
        var runGate = new object();
        CancellationTokenSource? runningCancellation = null;
        Guid? runningId = null;
        var runner = new LocalBackupRunner(executor, new BackupCompletion(storage, catalog), history, Path.Combine(directory, "work"), progress: snapshot =>
        {
            lock (runGate) runningId = snapshot.Finished ? null : snapshot.RunId;
            try { progressStore.Write(snapshot); warnings.Set("Состояние выполнения задания", null); }
            catch (Exception error) when (AgentRuntimeWarnings.Recoverable(error)) { warnings.Set("Состояние выполнения задания", error); }
        });
        var control = new LocalAgentControlStore(directory);
        var now = DateTimeOffset.UtcNow;
        var state = new LocalAgentState(Guid.NewGuid(), Environment.ProcessId, now, now, LocalAgentPhase.Running) { CanCancelCurrentRun = true, CanRunManually = manual is null };
        try { await control.WriteAsync(state, cancellationToken); }
        catch (Exception error) when (manual is null && AgentRuntimeWarnings.Recoverable(error)) { warnings.Set("Управление агентом", error); }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        string? lastError = null;
        string? monitorError = null;
        string? notificationError = null;
        string? hubError = null;
        BackupRun? lastRun = null;
        var dispatcher = new LocalBackupDispatcher(store, async (job, token) =>
        {
            using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            lock (runGate) { runningCancellation = runCancellation; runningId = null; }
            try { lastRun = await runner.RunAsync(job, runCancellation.Token); }
            finally { lock (runGate) { runningCancellation = null; runningId = null; } }
            Volatile.Write(ref lastError, lastRun.Status is BackupRunStatus.Failed or BackupRunStatus.PartiallySucceeded ? job.Name + ": " + lastRun.Error
                : lastRun.Warnings?.Count > 0 ? job.Name + ": " + lastRun.Warnings[0] : null);
            if (report is not null) await report(lastRun);
        });

        async Task ControlLoop()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                try
                {
                    if (await control.StopRequestedAsync(state.SessionId, lifetime.Token))
                    {
                        try { await control.WriteAsync(state with { Phase = LocalAgentPhase.Stopping, UpdatedUtc = DateTimeOffset.UtcNow }, CancellationToken.None); }
                        catch (Exception error) when (AgentRuntimeWarnings.Recoverable(error)) { warnings.Set("Управление агентом", error); }
                        finally { DiagnosticLog.Current.Write(DiagnosticEvent.AgentStopRequested); lifetime.Cancel(); }
                        return;
                    }
                    var cancel = await control.ReadCancelRunAsync(lifetime.Token);
                    if (cancel?.SessionId == state.SessionId)
                    {
                        lock (runGate)
                            if (cancel.RunId == runningId) runningCancellation?.Cancel();
                    }
                    warnings.Set("Чтение команд агента", null);
                }
                catch (Exception error) when (!lifetime.IsCancellationRequested && AgentRuntimeWarnings.Recoverable(error))
                { warnings.Set("Чтение команд агента", error); }
                try
                {
                    var notices = new[] { warnings.Summary, Volatile.Read(ref lastError), Volatile.Read(ref monitorError), Volatile.Read(ref notificationError), Volatile.Read(ref hubError) }.Where(text => !string.IsNullOrEmpty(text)).ToArray();
                    await control.WriteAsync(state with { UpdatedUtc = DateTimeOffset.UtcNow, Error = Volatile.Read(ref lastError),
                        MonitoringError = Volatile.Read(ref monitorError), Warning = notices.Length == 0 ? null : string.Join("\n", notices) }, lifetime.Token);
                    warnings.Set("Управление агентом", null);
                }
                catch (Exception error) when (!lifetime.IsCancellationRequested && AgentRuntimeWarnings.Recoverable(error))
                { warnings.Set("Управление агентом", error); }
            }
        }

        var controlTask = ControlLoop();
        async Task InitializeExecutionAsync()
        {
            while (true)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                try
                {
                    settings ??= await new AgentSettingsStore(Path.Combine(directory, "agent.json")).LoadAsync(lifetime.Token);
                    if (settings.Mode != AgentMode.Standalone) throw new NotSupportedException("Локальный фоновый агент поддерживает только автономный режим.");
                    await history.RecoverInterruptedAsync(lifetime.Token);
                    warnings.Set("Настройки агента", null); warnings.Set("История запусков", null);
                    return;
                }
                catch (Exception error) when (manual is null && !lifetime.IsCancellationRequested && AgentRuntimeWarnings.Recoverable(error))
                { warnings.Set(settings is null ? "Настройки агента" : "История запусков", error); }
                await Task.Delay(pollInterval ?? TimeSpan.FromSeconds(30), lifetime.Token);
            }
        }
        var initialized = InitializeExecutionAsync();
        async Task MonitorLoop()
        {
            await initialized;
            var monitorStore = new ExternalBackupMonitorStore(Path.Combine(directory, "external-monitors.json"));
            var poller = new ExternalBackupPoller(monitorStore,
                new ExternalBackupOutbox(Path.Combine(directory, "external-backups.json")), settings!,
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
                        else
                        {
                            if (!failures.ContainsKey(result.MonitorId)) DiagnosticLog.Current.Write(DiagnosticEvent.MonitoringFailed, DiagnosticLevel.Warning);
                            failures[result.MonitorId] = result.MonitorName + ": " + result.Error;
                        }
                    Volatile.Write(ref monitorError, failures.Count == 0 ? null : string.Join("; ", failures.Values));
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                catch (Exception ex) { DiagnosticLog.Current.Write(DiagnosticEvent.MonitoringFailed, DiagnosticLevel.Error, ex); Volatile.Write(ref monitorError, "Мониторинг SQL: " + ex.Message); }
            } while (await timer.WaitForNextTickAsync(lifetime.Token));
        }
        async Task ExecuteLoop()
        {
            await initialized;
            if (manual is not null) { await dispatcher.ExecuteAsync(manual, lifetime.Token); return; }
            await dispatcher.RunScheduleAsync(results =>
            {
                warnings.Set("Запуск по расписанию", results.FirstOrDefault(result => result.Error is not null)?.Error);
                return Task.CompletedTask;
            }, lifetime.Token, pollInterval, reportWarning: error =>
            {
                warnings.Set("Чтение расписания", error); return Task.CompletedTask;
            });
        }
        var executionTask = ExecuteLoop();
        var monitoringTask = manual is null ? MonitorLoop() : Task.CompletedTask;
        async Task NotificationLoop()
        {
            using var client = BotNotificationSender.CreateClient();
            IFtpCredentialStore secrets = OperatingSystem.IsWindows() ? new WindowsNotificationCredentialStore() : credentials;
            var notifications = new BackupNotificationDispatcher(directory, new BotNotificationSender(client, secrets));
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            do { Volatile.Write(ref notificationError, await notifications.PollAsync(lifetime.Token)); }
            while (await timer.WaitForNextTickAsync(lifetime.Token));
        }
        var serverMonitoringTask = manual is null ? new ServerMonitoringDispatcher(directory,credentials)
            .RunAsync(error=>warnings.Set("Сервер мониторинга",error),lifetime.Token) : Task.CompletedTask;
        async Task ManualRequestLoop()
        {
            await initialized;
            var queue = new LocalManualRunQueue(directory);
            var pendingReceipts = new Dictionary<Guid, LocalManualRunReceipt>();
            var failures = new Dictionary<Guid, (DateTimeOffset RetryAfter, Exception Error)>();
            async Task FlushReceipt(LocalManualRunReceipt receipt)
            {
                pendingReceipts[receipt.RequestId] = receipt;
                await queue.WriteReceiptAsync(receipt, CancellationToken.None);
                pendingReceipts.Remove(receipt.RequestId);
            }
            async Task TryCommand(Guid id, Func<Task> action)
            {
                if (failures.TryGetValue(id, out var failed) && failed.RetryAfter > DateTimeOffset.UtcNow) return;
                try { await action(); failures.Remove(id); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    failures[id] = (DateTimeOffset.UtcNow.AddSeconds(2), error);
                    warnings.Set("Очередь ручных запусков", error);
                }
            }
            async Task HandleRequest(LocalManualRunRequest request)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                var receipt = new LocalManualRunReceipt(request.RequestId, request.SessionId, request.JobId, LocalManualRunPhase.Accepted);
                if (request.SessionId != state.SessionId)
                {
                    await FlushReceipt(receipt with { Phase = LocalManualRunPhase.Rejected, Error = "Сеанс агента завершён. Повторите ручной запуск." });
                    queue.Consume(request.RequestId); return;
                }
                LocalManualRunReceipt? existing;
                try { existing = await queue.ReadReceiptAsync(request.RequestId, lifetime.Token); }
                catch (Exception error) when (error is System.Text.Json.JsonException or InvalidDataException)
                {
                    await FlushReceipt(receipt with { Phase = LocalManualRunPhase.Rejected, Error = "Подтверждение команды повреждено. Проверьте историю перед повторным запуском." });
                    queue.Consume(request.RequestId); return;
                }
                if (existing is { Phase: not LocalManualRunPhase.Accepted }) { queue.Consume(request.RequestId); return; }
                var job = (await store.LoadAsync(lifetime.Token)).FirstOrDefault(j => j.Id == request.JobId);
                if (job is null)
                {
                    await FlushReceipt(receipt with { Phase = LocalManualRunPhase.Rejected, Error = "Сохранённое задание не найдено." });
                    queue.Consume(request.RequestId); return;
                }
                // Never run without a durable acceptance; remove the command before execution to avoid replay.
                await queue.WriteReceiptAsync(receipt, lifetime.Token);
                queue.Consume(request.RequestId);
                LocalManualRunReceipt completedReceipt;
                try
                {
                    await dispatcher.ExecuteAsync(job, lifetime.Token);
                    lifetime.Token.ThrowIfCancellationRequested();
                    completedReceipt = receipt with { Phase = LocalManualRunPhase.Finished };
                    warnings.Set("Ручной запуск", null);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                { await FlushReceipt(receipt with { Phase = LocalManualRunPhase.Interrupted, Error = "Агент остановлен. Проверьте историю запусков." }); throw; }
                catch (Exception error)
                {
                    completedReceipt = receipt with { Phase = LocalManualRunPhase.Rejected, Error = "Запуск не завершён. Проверьте историю и журнал агента." };
                    warnings.Set("Ручной запуск", error);
                }
                await FlushReceipt(completedReceipt);
            }
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            do
            {
                try
                {
                    foreach (var receipt in pendingReceipts.Values.ToArray())
                        await TryCommand(receipt.RequestId, () => FlushReceipt(receipt));
                    var requests = await queue.PendingAsync(lifetime.Token);
                    var outstanding = requests.Select(r => r.RequestId).Concat(pendingReceipts.Keys).ToHashSet();
                    foreach (var removed in failures.Keys.Where(id => !outstanding.Contains(id)).ToArray()) failures.Remove(removed);
                    foreach (var request in requests)
                        await TryCommand(request.RequestId, () => HandleRequest(request));
                    warnings.Set("Очередь ручных запусков", failures.Values.Select(f => f.Error).FirstOrDefault());
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                catch (Exception error) { warnings.Set("Очередь ручных запусков", error); }
            } while (await timer.WaitForNextTickAsync(lifetime.Token));
        }
        var manualRequestsTask = manual is null ? ManualRequestLoop() : Task.CompletedTask;
        var notificationTask = manual is null ? NotificationLoop() : Task.CompletedTask;
        var hubTask = manual is null ? new OneCBackup.Remote.EmbeddedHub(directory).RunAsync(warning => Volatile.Write(ref hubError, warning), lifetime.Token) : Task.CompletedTask;
        Exception? failure = null;
        try
        {
            var completed = manual is null
                ? await Task.WhenAny(executionTask, controlTask, monitoringTask, manualRequestsTask)
                : await Task.WhenAny(executionTask, controlTask);
            await completed;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { DiagnosticLog.Current.Write(DiagnosticEvent.AgentFailed, DiagnosticLevel.Error, ex); failure = ex; }
        finally
        {
            lifetime.Cancel();
            try { await Task.WhenAll(executionTask, controlTask, monitoringTask, notificationTask, hubTask, serverMonitoringTask, manualRequestsTask); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception ex) { failure ??= ex; }
            var error = failure?.Message ?? (manual is not null && (lastRun?.Status is BackupRunStatus.Failed or BackupRunStatus.PartiallySucceeded) ? lastRun.Error : null);
            try { await control.WriteAsync(state with { UpdatedUtc = DateTimeOffset.UtcNow,
                Phase = error is null ? LocalAgentPhase.Stopped : LocalAgentPhase.Faulted, Error = error,
                MonitoringError = Volatile.Read(ref monitorError), Warning = warnings.Summary }, CancellationToken.None); }
            catch (Exception writeError) when (AgentRuntimeWarnings.Recoverable(writeError)) { warnings.Set("Запись состояния агента", writeError); }
        }
        DiagnosticLog.Current.Write(DiagnosticEvent.AgentStopped, failure is null ? DiagnosticLevel.Info : DiagnosticLevel.Error);
        return failure is not null || manual is not null && (lastRun?.Status is BackupRunStatus.Failed or BackupRunStatus.PartiallySucceeded) ? 1 : 0;
    }
}
