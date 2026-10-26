using System.IO;
using System.Windows;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    internal async Task SmokeVerifyCrashProgressAsync()
    {
        if (!SmokeMode) throw new InvalidOperationException("This check requires an isolated desktop smoke profile.");
        var timerEnabled = backgroundTimer.IsEnabled;
        backgroundTimer.Stop();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (refreshingBackground)
        {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("Окно не завершило чтение состояния агента перед проверкой прогресса.");
            await Task.Delay(10);
        }
        var previousState = backgroundState; var previousBusy = backgroundBusy; var previousProgress = latestBackupProgress;
        var path = Path.Combine(DesktopPaths.DataDirectory, "backup-progress.json");
        var previousFile = File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
        try
        {
            var now = DateTimeOffset.UtcNow;
            backgroundBusy = true;
            backgroundState = new(Guid.NewGuid(), int.MaxValue - 100, now.AddHours(-2), now, LocalAgentPhase.Running)
                { CanCancelCurrentRun = true, CanRunManually = true, RunsSchedules = true };
            var sql = new BackupProgressSnapshot(Guid.NewGuid(), Guid.NewGuid(), "Долгая SQL-копия", backgroundState.ProcessId,
                1, now.AddHours(-1), new(BackupStage.CreatingBackup), []);
            backupProgressStore.Write(sql);
            await RefreshBackupProgressAsync();
            if (BackupProgressPanel.Visibility != Visibility.Visible || !BackupProgressBar.IsIndeterminate
                || !TrayAnimationRunning || !TrayCurrentRunStopAvailable)
                throw new InvalidOperationException("Долгая SQL-копия действующего агента скрыта из-за отсутствия обновлений байтов.");

            // The old file remains while a new ready process already owns the profile.
            backgroundState = backgroundState with { SessionId = Guid.NewGuid(), ProcessId = backgroundState.ProcessId - 1, StartedUtc = now };
            UpdateTray();
            if (TrayAnimationRunning || TrayCurrentRunStopAvailable || trayIcon!.Text.Contains("Получение копии"))
                throw new InvalidOperationException("Трей показывает задание аварийно завершившегося агента после запуска нового процесса.");
            await RefreshBackupProgressAsync();
            if (BackupProgressPanel.Visibility != Visibility.Collapsed || latestBackupProgress is not null)
                throw new InvalidOperationException("Прогресс прежнего процесса остался на экране при работающем новом агенте.");

            // PID reuse must not make the previous process's file current again.
            backgroundState = backgroundState with { ProcessId = sql.ProcessId };
            await RefreshBackupProgressAsync();
            if (BackupProgressPanel.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Повторно использованный PID оживил прогресс прежнего запуска.");

            var completed = sql with { RunId = Guid.NewGuid(), UpdatedUtc = DateTimeOffset.UtcNow, Progress = new(BackupStage.Completed) };
            backupProgressStore.Write(completed);
            await RefreshBackupProgressAsync();
            if (BackupProgressPanel.Visibility != Visibility.Visible || TrayAnimationRunning || TrayCurrentRunStopAvailable)
                throw new InvalidOperationException("Свежий итог задания должен оставаться видимым без анимации и остановки.");
            var expired = completed with { Sequence = 2, UpdatedUtc = DateTimeOffset.UtcNow.AddSeconds(-20) };
            RenderBackupProgress(expired);
            backupProgressStore.Write(expired);
            await RefreshBackupProgressAsync();
            if (BackupProgressPanel.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Итог задания не скрыт после периода отображения.");
        }
        finally
        {
            if (previousFile is null) File.Delete(path); else await File.WriteAllBytesAsync(path, previousFile);
            backgroundState = previousState; backgroundBusy = previousBusy;
            ClearBackupProgress();
            if (previousProgress is not null) RenderBackupProgress(previousProgress);
            if (timerEnabled) backgroundTimer.Start();
        }
    }
}
