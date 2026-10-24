using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using OneCBackup.Core;
using Forms = System.Windows.Forms;

namespace OneCBackup.Desktop;

public partial class MainWindow
{
    private Forms.NotifyIcon? trayIcon;
    private Forms.ContextMenuStrip? trayMenu;
    private Forms.ToolStripMenuItem? trayStop, trayExit, trayCancelRun;
    private System.Drawing.Icon? trayImage;
    private TrayAnimationIcons? trayAnimation;
    private System.Windows.Threading.DispatcherTimer? trayAnimationTimer;
    private int trayAnimationFrame;
    private bool trayCancelSending;
    private Guid? trayCancelRequestedRun;
    private bool trayStopping, allowFullClose, trayHintShown;
    internal bool SmokeMode { get; set; }
    internal bool TrayVisible => trayIcon?.Visible == true;
    internal bool TrayAnimationRunning => trayAnimationTimer?.IsEnabled == true;
    internal bool TrayCurrentRunStopAvailable => trayCancelRun?.Enabled == true;

    private void InitializeTray()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/one-c-backup-tray.ico"));
            if (resource is not null)
            {
                using (resource.Stream)
                using (var original = new System.Drawing.Icon(resource.Stream, Forms.SystemInformation.SmallIconSize)) trayImage = (System.Drawing.Icon)original.Clone();
            }
            trayImage ??= (System.Drawing.Icon)System.Drawing.SystemIcons.Shield.Clone();
            trayMenu = new Forms.ContextMenuStrip();
            trayMenu.Items.Add("Открыть 1C-Backup", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
            trayCancelRun = new Forms.ToolStripMenuItem("Остановить текущее задание");
            trayCancelRun.Click += async (_, _) => await CancelCurrentRunFromTrayAsync();
            trayMenu.Items.Add(trayCancelRun);
            trayStop = new Forms.ToolStripMenuItem("Остановить автоматическое копирование");
            trayStop.Click += async (_, _) => await StopFromTrayAsync(false);
            trayMenu.Items.Add(trayStop);
            trayMenu.Items.Add(new Forms.ToolStripSeparator());
            trayExit = new Forms.ToolStripMenuItem("Выйти полностью (остановить агент)");
            trayExit.Click += async (_, _) => await StopFromTrayAsync(true);
            trayMenu.Items.Add(trayExit);
            trayIcon = new Forms.NotifyIcon { Icon = trayImage, Text = "1C-Backup", ContextMenuStrip = trayMenu, Visible = true };
            trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
            // Animation failure must not remove the normal icon or its controls.
            try
            {
                trayAnimation = new TrayAnimationIcons(trayImage);
                trayAnimationTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                trayAnimationTimer.Tick += (_, _) =>
                {
                    if (trayIcon is not null && trayAnimation is not null)
                        trayIcon.Icon = trayAnimation.Frames[trayAnimationFrame++ % trayAnimation.Frames.Count];
                };
            }
            catch (Exception error) { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Warning, error); }
            UpdateTray();
        }
        catch (Exception error)
        {
            DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, error);
            DisposeTray();
            Status.Text = "Не удалось показать значок в трее. Окно останется открытым; фоновый агент можно остановить кнопкой «Остановить».";
        }
    }

    internal void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private async void WindowClosingToTray(object? sender, CancelEventArgs e)
    {
        if (allowFullClose || SmokeMode) return;
        if (trayIcon is { Visible: true })
        {
            e.Cancel = true;
            Hide();
            if (!trayHintShown)
            {
                trayHintShown = true;
                trayIcon.ShowBalloonTip(4000, "1C-Backup работает в трее",
                    "Чтобы остановить агент и закрыть программу, выберите «Выйти полностью» в меню значка.", Forms.ToolTipIcon.Info);
            }
            return;
        }
        if (persisting || jobFileTransfer || activeRun is not null || schedulingTask is not null || manualPending || copyVerification is not null)
        { e.Cancel = true; Status.Text = "Дождитесь завершения операции или остановите копирование перед закрытием."; return; }
        if (HasUnsavedJob)
        {
            e.Cancel = true;
            await Task.Yield(); // Complete WPF's Closing event before showing a dialog.
            if (!await ConfirmLeavingEditorAsync()) return;
            allowFullClose = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    private void DisposeTray()
    {
        trayAnimationTimer?.Stop(); trayAnimationTimer = null;
        if (trayIcon is not null) { trayIcon.Visible = false; trayIcon.Dispose(); trayIcon = null; }
        trayMenu?.Dispose(); trayMenu = null;
        trayImage?.Dispose(); trayImage = null;
        trayAnimation?.Dispose(); trayAnimation = null;
    }

    private void UpdateTray()
    {
        if (trayIcon is null) return;
        if (trayStop is not null) trayStop.Enabled = !trayStopping &&
            (backgroundStarting || backgroundBusy && backgroundState?.Phase is LocalAgentPhase.Running or LocalAgentPhase.Stopping || schedulingTask is not null);
        if (trayExit is not null) trayExit.Enabled = !trayStopping;
        var currentProgress = latestBackupProgress is { Finished: false } snapshot &&
            (snapshot.ProcessId == Environment.ProcessId || backgroundBusy && backgroundState is not null
                && snapshot.ProcessId == backgroundState.ProcessId && snapshot.UpdatedUtc >= backgroundState.StartedUtc);
        var working = activeRun is not null || currentProgress;
        if (working && trayAnimationTimer is { IsEnabled: false }) trayAnimationTimer.Start();
        else if (!working && trayAnimationTimer is { IsEnabled: true })
        { trayAnimationTimer.Stop(); trayAnimationFrame = 0; trayIcon.Icon = trayImage; }
        if (trayCancelRun is not null)
        {
            var canCancelBackground = currentProgress && backgroundState is { CanCancelCurrentRun: true, Phase: LocalAgentPhase.Running }
                && latestBackupProgress?.ProcessId == backgroundState.ProcessId && trayCancelRequestedRun != latestBackupProgress.RunId;
            trayCancelRun.Enabled = !trayStopping && !trayCancelSending && (activeRun is { IsCancellationRequested: false } || canCancelBackground);
            trayCancelRun.ToolTipText = working && activeRun is null && backgroundState?.CanCancelCurrentRun != true
                ? "Для остановки только текущего задания обновите и перезапустите агент." : "Расписание и агент продолжат работать.";
        }
        var text = trayStopping ? "1C-Backup — останавливается…" : activeRun is not null ? "1C-Backup — создаётся копия"
            : backgroundBusy || schedulingTask is not null ? "1C-Backup — автоматическое копирование включено" : "1C-Backup — автоматическое копирование выключено";
        if (!trayStopping && latestBackupProgress is { Finished: false } progress) text = "1C-Backup — " + StageLabel(progress.Progress);
        trayIcon.Text = text.Length <= 63 ? text : text[..63];
    }

    private async Task CancelCurrentRunFromTrayAsync()
    {
        if (trayCancelRun?.Enabled != true) return;
        if (activeRun is not null) { CancelRun_Click(this, new RoutedEventArgs()); UpdateTray(); return; }
        var selectedRun = latestBackupProgress;
        var selectedSession = backgroundState?.SessionId;
        trayCancelSending = true; UpdateTray();
        try
        {
            var state = await backgroundControl.ReadAsync();
            var progress = await backupProgressStore.ReadAsync();
            if (state is not { CanCancelCurrentRun: true, Phase: LocalAgentPhase.Running } || state.SessionId != selectedSession
                || progress is null || progress.Finished || progress.RunId != selectedRun?.RunId
                || progress.ProcessId != state.ProcessId || progress.UpdatedUtc < state.StartedUtc)
            { Status.Text = "Выбранное задание уже завершилось. Следующее задание не остановлено."; return; }
            await backgroundControl.RequestCancelRunAsync(state.SessionId, progress.RunId);
            trayCancelRequestedRun = progress.RunId;
            Status.Text = "Запрошена остановка текущего задания. Агент и расписание продолжат работать.";
        }
        catch (Exception error)
        { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, error); ShowFromTray(); Status.Text = "Не удалось остановить текущее задание: " + error.Message; }
        finally { trayCancelSending = false; UpdateTray(); }
    }

    private async Task StopFromTrayAsync(bool exit, Func<Task>? beforeClose = null)
    {
        if (trayStopping) return;
        if (serviceManaging) { ShowFromTray(); Status.Text = "Дождитесь завершения установки или удаления службы."; return; }
        if (OwnedWindows.Cast<Window>().Any(window => window.IsVisible))
        { ShowFromTray(); Status.Text = "Сначала закройте окно настроек или мониторинга, затем повторите остановку из трея."; return; }
        // Resolve the draft before stopping any running work or handing off an update.
        if (exit && !await ConfirmLeavingEditorAsync()) return;
        if (exit) updateCancellation?.Cancel();
        trayStopping = true;
        IsEnabled = false;
        UpdateTray();
        try
        {
            Status.Text = exit ? "Полный выход: останавливается копирование и записываются результаты…" : "Останавливается автоматическое копирование…";
            schedulerCancellation?.Cancel();
            if (exit) { CancelQueuedManualRuns(); activeRun?.Cancel(); copyVerification?.Cancel(); }
            var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
            while (schedulingTask is not null || exit && (updateBusy || savingUpdatePreferences || activeRun is not null || manualPending || copyVerification is not null || persisting || jobFileTransfer))
            {
                if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("Текущая операция ещё завершается. Программа остаётся в трее; дождитесь записи результата и повторите выход.");
                await Task.Delay(100);
            }
            // A just-launched agent may not have taken the lease yet.
            while (backgroundStarting && launchedAgent is not null && !launchedAgent.HasExited)
            {
                await RefreshBackgroundStateAsync();
                if (!backgroundStarting) break;
                if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("Запуск агента ещё не завершён. Проверьте его состояние и повторите остановку.");
                await Task.Delay(200);
            }
            await StopOwnedServiceAsync();
            var stopped = await new LocalAgentStopper(DesktopPaths.DataDirectory).StopAsync(TimeSpan.FromMinutes(2));
            if (stopped is not null && stopped.ProcessId != Environment.ProcessId)
            {
                try
                {
                    using var process = Process.GetProcessById(stopped.ProcessId);
                    // Do not wait for an unrelated process that reused the old PID.
                    if (process.StartTime.ToUniversalTime() <= stopped.StartedUtc.UtcDateTime)
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                }
                catch (ArgumentException) { /* The stopped process no longer exists. */ }
            }
            await RefreshBackgroundStateAsync();
            Status.Text = "Копирование остановлено. Рабочий профиль освобождён.";
            if (exit)
            {
                if (beforeClose is not null) await beforeClose();
                else if (updateSettings.UpdateOnExit && preparedUpdatePath is not null && availableUpdate is not null)
                    await ApplicationUpdateHandoff.StartAsync(availableUpdate, preparedUpdatePath, restart: false, restartAgent: false);
                allowFullClose = true;
                Close();
            }
        }
        catch (Exception error)
        {
            DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, error);
            ShowFromTray();
            Status.Text = "Полная остановка не подтверждена: " + error.Message;
        }
        finally { trayStopping = false; IsEnabled = true; if (!allowFullClose) UpdateRunControls(); }
    }

    private async void ExitFully_Click(object sender, RoutedEventArgs e) => await StopFromTrayAsync(true);
}
