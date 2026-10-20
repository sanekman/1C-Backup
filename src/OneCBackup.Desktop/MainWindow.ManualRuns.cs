using OneCBackup.Core;

namespace OneCBackup.Desktop;

public partial class MainWindow
{
    internal bool SmokeBackgroundManualReady => BackgroundAcceptsManualRuns;
    private readonly LocalManualRunQueue manualQueue = new(DesktopPaths.DataDirectory);
    private readonly Dictionary<Guid, CancellationTokenSource> localManualRuns = [];
    private readonly Dictionary<Guid, LocalManualRunRequest> backgroundManualRuns = [];
    private bool submittingManualRun;

    private bool BackgroundAcceptsManualRuns => backgroundBusy && !backgroundStarting
        && backgroundState is { Phase: LocalAgentPhase.Running, CanRunManually: true }
        && DateTimeOffset.UtcNow - backgroundState.UpdatedUtc < TimeSpan.FromSeconds(10);

    private void UpdateManualRunControl()
    {
        var selected = Jobs.SelectedItem as BackupJob;
        var queued = selected is not null && (localManualRuns.ContainsKey(selected.Id)
            || backgroundManualRuns.Values.Any(r => r.JobId == selected.Id && r.SessionId == backgroundState?.SessionId));
        RunButton.IsEnabled = ready && canSave && !editorOpen && !serviceManaging && !installingUpdate
            && !trayStopping && !persisting && !jobFileTransfer && !schedulerStopping && copyVerification is null
            && !submittingManualRun && !backgroundStarting && (!backgroundBusy || BackgroundAcceptsManualRuns)
            && selected is not null && !queued;
        RunButton.ToolTip = queued ? "Ручной запуск этого задания уже принят. Другие задания можно добавить в очередь."
            : backgroundBusy && !BackgroundAcceptsManualRuns ? "Дождитесь готовности агента. Если используется старая версия, обновите и перезапустите агент."
            : "Выполнить сохранённое задание вручную. Если копирование уже идёт, задание дождётся своей очереди. Расписание продолжит работать.";
    }

    private async Task RunManualAsync(BackupJob job)
    {
        if (backgroundBusy)
        {
            if (!BackgroundAcceptsManualRuns) return;
            submittingManualRun = true;
            UpdateRunControls();
            try
            {
                var request = await manualQueue.EnqueueAsync(backgroundState!.SessionId, job.Id);
                backgroundManualRuns[request.RequestId] = request;
                Status.Text = "Ручной запуск передан агенту: " + job.Name + ". Если копирование уже идёт, задание дождётся своей очереди.";
            }
            catch (Exception error) { ReportManualRunError(error); }
            finally { submittingManualRun = false; UpdateRunControls(); }
            return;
        }
        using var cancellation = new CancellationTokenSource();
        localManualRuns.Add(job.Id, cancellation);
        manualPending = true;
        UpdateRunControls();
        if (activeRun is not null) Status.Text = "Ручной запуск добавлен в очередь: " + job.Name;
        try { await dispatcher.ExecuteAsync(job, cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { ReportManualRunError(error); }
        finally { localManualRuns.Remove(job.Id); manualPending = localManualRuns.Count != 0; UpdateRunControls(); }
    }

    private void ReportManualRunError(Exception error)
    {
        DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, error);
        Status.Text = "Не удалось запустить задание: " + error.Message;
    }

    private async Task RefreshManualRequestsAsync()
    {
        foreach (var request in await manualQueue.PendingAsync())
            if (request.SessionId == backgroundState?.SessionId) backgroundManualRuns.TryAdd(request.RequestId, request);
        foreach (var request in backgroundManualRuns.Values.ToArray())
        {
            var receipt = await manualQueue.ReadReceiptAsync(request.RequestId);
            if (receipt is { Phase: not LocalManualRunPhase.Accepted })
            {
                backgroundManualRuns.Remove(request.RequestId);
                Status.Text = receipt.Phase == LocalManualRunPhase.Finished
                    ? "Попытка ручного копирования завершена. Результат доступен в истории." : receipt.Error;
                await RefreshHistoryAsync();
                await RefreshCopiesAsync();
            }
            else if (backgroundState?.SessionId != request.SessionId || !backgroundBusy
                || backgroundState.Phase is LocalAgentPhase.Stopped or LocalAgentPhase.Faulted)
            {
                backgroundManualRuns.Remove(request.RequestId);
                Status.Text = "Сеанс агента завершён. Проверьте историю перед повторным ручным запуском.";
            }
        }
    }

    private void CancelQueuedManualRuns()
    {
        foreach (var cancellation in localManualRuns.Values) cancellation.Cancel();
    }
}
