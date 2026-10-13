using System.Windows;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

public partial class MainWindow
{
    private readonly BackupProgressStore backupProgressStore = new(DesktopPaths.DataDirectory);
    private BackupProgressSnapshot? latestBackupProgress;
    private static string StageLabel(BackupStageProgress value)
    {
        var label = value.Stage switch
        {
            BackupStage.ConnectingSource => "Подключение к источнику",
            BackupStage.PreparingStorage => "Подготовка временной папки и хранилища",
            BackupStage.ScanningSource => "Получение списка файлов",
            BackupStage.CreatingBackup => "Получение копии из источника",
            BackupStage.ProcessingFiles => "Обработка файлов (по числу файлов)",
            BackupStage.ArchivingFile => "Архивация текущего файла",
            BackupStage.ArchivingFiles => "Архивация файлов",
            BackupStage.DownloadingAndArchivingFiles => "Скачивание и архивация файлов",
            BackupStage.DownloadingFile => "Скачивание текущего файла",
            BackupStage.Archiving => "Архивация",
            BackupStage.ConnectingStorage => "Подключение к хранилищу",
            BackupStage.CopyingToStorage => "Копирование в хранилище",
            BackupStage.Verifying => "Проверка содержимого копии",
            BackupStage.Retention => "Проверка и удаление старых копий",
            BackupStage.Waiting => "Ожидание отправки",
            BackupStage.Completed => "Готово",
            BackupStage.Cancelled => "Отменено",
            BackupStage.PartiallySucceeded => "Завершено с ошибками доставки",
            _ => "Ошибка — подробности в результате задания"
        };
        return label + (value.Percent is int percent ? $" · {percent}%" : value.Stage is BackupStage.Cancelled or BackupStage.Failed or BackupStage.PartiallySucceeded ? "" : "…");
    }
    internal void RenderBackupProgress(BackupProgressSnapshot value)
    {
        if (latestBackupProgress is { } previous && previous.RunId == value.RunId && previous.Sequence >= value.Sequence) return;
        latestBackupProgress = value;
        BackupProgressPanel.Visibility = Visibility.Visible;
        BackupProgressLabel.Text = value.JobName + " · " + StageLabel(value.Progress)
            + (value.Progress.Stage == BackupStage.Archiving && value.Destinations.Count == 0 ? " (текущий этап)" : "");
        BackupProgressBar.IsIndeterminate = value.Progress.Percent is null && !value.Finished;
        BackupProgressBar.Value = value.Progress.Percent ?? 0;
        BackupDestinationProgressList.ItemsSource = value.Destinations.Select(d => new
        {
            Label = $"Хранилище {d.Index} · " + (d.Kind == StorageKind.GoogleDrive ? "Google Drive" : d.Kind == StorageKind.Ftp ? "FTP" : d.Kind == StorageKind.NetworkShare ? "сетевая папка" : "локальный диск") + " · " + StageLabel(d.Progress),
            Percent = d.Progress.Percent ?? 0,
            Indeterminate = d.Progress.Percent is null && d.Progress.Stage is not (BackupStage.Cancelled or BackupStage.Failed)
        }).ToArray();
        JobProgress.Visibility = Visibility.Collapsed;
        UpdateTray();
    }
    private void ClearBackupProgress()
    {
        latestBackupProgress = null;
        BackupProgressPanel.Visibility = Visibility.Collapsed;
        BackupDestinationProgressList.ItemsSource = null;
    }
    private async Task RefreshBackupProgressAsync()
    {
        if (activeRun is not null) return;
        var value = await backupProgressStore.ReadAsync();
        // Ignore snapshots from an earlier process/session. A long SQL dump may have no byte updates.
        if (value is not null && backgroundBusy && backgroundState is not null
            && value.ProcessId == backgroundState.ProcessId && value.UpdatedUtc >= backgroundState.StartedUtc
            && (!value.Finished || DateTimeOffset.UtcNow - value.UpdatedUtc < TimeSpan.FromSeconds(15)))
            RenderBackupProgress(value);
        else if (latestBackupProgress is { } latest && (latest.ProcessId != Environment.ProcessId && !backgroundBusy
            || latest.Finished && DateTimeOffset.UtcNow - latest.UpdatedUtc > TimeSpan.FromSeconds(15))) ClearBackupProgress();
    }
}
