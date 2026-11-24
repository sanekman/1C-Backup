using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private readonly Stopwatch exportClock = new();
    private readonly DispatcherTimer exportProgressTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private BackupExportProgress? latestExportProgress;
    private FileRestoreProgress? latestFileRestoreProgress;
    private int exportProgressGeneration;
    internal Action<BackupExportProgress>? CopyExportProgressObserved { get; set; }

    private IProgress<BackupExportProgress> BeginCopyExportProgress()
    {
        var generation = ++exportProgressGeneration;
        latestExportProgress = null;
        latestFileRestoreProgress = null;
        exportClock.Restart();
        CopyExportProgressPanel.Visibility = Visibility.Visible;
        CopyExportProgressBar.IsIndeterminate = true;
        CopyExportProgressLabel.Text = "Подключение к хранилищу…";
        exportProgressTimer.Tick -= ExportProgressTimer_Tick;
        exportProgressTimer.Tick += ExportProgressTimer_Tick;
        exportProgressTimer.Start();
        return new Progress<BackupExportProgress>(value =>
        {
            if (generation != exportProgressGeneration) return;
            latestExportProgress = value;
            RenderCopyExportProgress();
            CopyExportProgressObserved?.Invoke(value);
        });
    }

    private void ExportProgressTimer_Tick(object? sender, EventArgs e) => RenderCopyExportProgress();

    private void RenderCopyExportProgress()
    {
        if (latestFileRestoreProgress is { } restore) { RenderFileRestoreProgress(restore); return; }
        var value = latestExportProgress;
        var elapsed = exportClock.Elapsed;
        var time = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"mm\:ss");
        var phase = value?.Phase switch
        {
            BackupExportPhase.Transferring => "Копирование файла",
            BackupExportPhase.Verifying => "Проверка SHA-256",
            BackupExportPhase.Publishing => "Сохранение готового файла",
            BackupExportPhase.Completed => "Файл сохранён",
            _ => "Подключение к хранилищу"
        };
        var cancelled = copyVerification?.IsCancellationRequested == true;
        CopyExportProgressLabel.Text = cancelled ? "Отмена: ожидается остановка передачи и удаление временного файла · " + time
            : phase + (value is null ? "" : $" · {ExportSize(value.BytesCompleted)} из {ExportSize(value.TotalBytes)}") + " · " + time;
        CopyExportProgressBar.IsIndeterminate = cancelled || value is null || value.TotalBytes == 0
            || value.Phase is BackupExportPhase.Preparing or BackupExportPhase.Publishing;
        CopyExportProgressBar.Value = value is null || value.TotalBytes == 0 ? 0
            : Math.Clamp((double)value.BytesCompleted / value.TotalBytes, 0, 1);
    }

    private void EndCopyExportProgress()
    {
        ++exportProgressGeneration;
        exportProgressTimer.Stop(); exportClock.Stop();
        CopyExportProgressPanel.Visibility = Visibility.Collapsed;
    }

    private static string ExportSize(long size) => size >= 1024L * 1024 * 1024 ? $"{size / (1024d * 1024 * 1024):N1} ГБ"
        : size >= 1024L * 1024 ? $"{size / (1024d * 1024):N1} МБ" : size >= 1024 ? $"{size / 1024d:N1} КБ" : $"{size} Б";
}
