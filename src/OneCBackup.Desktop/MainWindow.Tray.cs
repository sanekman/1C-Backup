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
    private Forms.ToolStripMenuItem? trayStop, trayExit;
    private System.Drawing.Icon? trayImage;
    private bool trayStopping, allowFullClose, trayHintShown;
    internal bool SmokeMode { get; set; }
    internal bool TrayVisible => trayIcon?.Visible == true;

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
            trayStop = new Forms.ToolStripMenuItem("Остановить автоматическое копирование");
            trayStop.Click += async (_, _) => await StopFromTrayAsync(false);
            trayMenu.Items.Add(trayStop);
            trayMenu.Items.Add(new Forms.ToolStripSeparator());
            trayExit = new Forms.ToolStripMenuItem("Выйти полностью (остановить агент)");
            trayExit.Click += async (_, _) => await StopFromTrayAsync(true);
            trayMenu.Items.Add(trayExit);
            trayIcon = new Forms.NotifyIcon { Icon = trayImage, Text = "1C-Backup", ContextMenuStrip = trayMenu, Visible = true };
            trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
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

    private void WindowClosingToTray(object? sender, CancelEventArgs e)
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
        { e.Cancel = true; Status.Text = "Дождитесь завершения операции или остановите копирование перед закрытием."; }
    }

    private void DisposeTray()
    {
        if (trayIcon is not null) { trayIcon.Visible = false; trayIcon.Dispose(); trayIcon = null; }
        trayMenu?.Dispose(); trayMenu = null;
        trayImage?.Dispose(); trayImage = null;
    }

    private void UpdateTray()
    {
        if (trayIcon is null) return;
        if (trayStop is not null) trayStop.Enabled = !trayStopping &&
            (backgroundStarting || backgroundBusy && backgroundState?.Phase is LocalAgentPhase.Running or LocalAgentPhase.Stopping || schedulingTask is not null);
        if (trayExit is not null) trayExit.Enabled = !trayStopping;
        var text = trayStopping ? "1C-Backup — останавливается…" : activeRun is not null ? "1C-Backup — создаётся копия"
            : backgroundBusy || schedulingTask is not null ? "1C-Backup — автоматическое копирование включено" : "1C-Backup — автоматическое копирование выключено";
        if (!trayStopping && latestBackupProgress is { Finished: false } progress) text = "1C-Backup — " + StageLabel(progress.Progress);
        trayIcon.Text = text.Length <= 63 ? text : text[..63];
    }

    private async Task StopFromTrayAsync(bool exit)
    {
        if (trayStopping) return;
        if (OwnedWindows.Cast<Window>().Any(window => window.IsVisible))
        { ShowFromTray(); Status.Text = "Сначала закройте окно настроек или мониторинга, затем повторите остановку из трея."; return; }
        trayStopping = true;
        IsEnabled = false;
        UpdateTray();
        try
        {
            Status.Text = exit ? "Полный выход: останавливается копирование и записываются результаты…" : "Останавливается автоматическое копирование…";
            schedulerCancellation?.Cancel();
            if (exit) { activeRun?.Cancel(); copyVerification?.Cancel(); }
            var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
            while (schedulingTask is not null || exit && (activeRun is not null || manualPending || copyVerification is not null || persisting || jobFileTransfer))
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
