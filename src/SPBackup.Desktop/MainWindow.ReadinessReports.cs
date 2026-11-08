using System.Windows;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private string? readinessJobFingerprint;
    private bool readinessReportStale;

    private void BeginReadinessReport(BackupJob job)
    {
        storageCheckJobId = job.Id;
        readinessJobFingerprint = LocalStorageCheckRequest.Fingerprint(job);
        readinessReportStale = false;
        ReadinessReportActionStatus.Text = "";
    }

    private void UpdateReadinessReportVisibility()
    {
        if (StorageCheckResults is null || CopyReadinessReportButton is null) return;
        var hasResult = !checkingStorages && !jobFileTransfer && !editorOpen
            && Jobs.SelectedItem is BackupJob current && current.Id == storageCheckJobId && StorageCheckDetails.Text.Length > 0;
        CopyReadinessReportButton.IsEnabled = SaveReadinessReportButton.IsEnabled = ClearReadinessReportButton.IsEnabled = hasResult;
        if (checkingStorages)
        {
            ReadinessReportState.Text = "Проверка выполняется. Итоговый отчёт ещё не готов.";
            return;
        }
        var selected = Jobs.SelectedItem as BackupJob;
        StorageCheckResults.Visibility = selected?.Id == storageCheckJobId && selected is not null
            ? Visibility.Visible : Visibility.Collapsed;
        ReadinessReportState.Text = readinessReportStale ? "Настройки изменились — повторите проверку." : "Результат на момент проверки. Изменения данных, прав доступа и сети после неё не проверялись.";
        if (selected is null || selected.Id != storageCheckJobId || readinessJobFingerprint is null || readinessReportStale) return;
        if (LocalStorageCheckRequest.Fingerprint(selected) == readinessJobFingerprint) return;
        readinessReportStale = true;
        ReadinessReportState.Text = "Настройки изменились — повторите проверку.";
        StorageCheckResults.Header = "Результат относится к прежним настройкам: " + selected.Name;
        StorageCheckDetails.Text = "Настройки задания изменились после проверки. Выполните проверку заново перед использованием результата.\n\n" + StorageCheckDetails.Text;
        StorageCheckResults.IsExpanded = true;
    }

    private string? ReadinessUnavailableReason(bool idle)
    {
        if (!ready) return "Дождитесь загрузки настроек агента.";
        if (editorOpen) return "Сохраните или закройте мастер задания, затем выберите задание в списке.";
        if (Jobs.SelectedItem is ExternalBackupTask) return "Это наблюдение за внешними копиями. Используйте «Проверить сейчас» или «Результат».";
        if (Jobs.SelectedItem is not BackupJob) return "Выберите задание резервного копирования в таблице.";
        if (!idle) return checkingStorages ? "Дождитесь текущей проверки или отмените её кнопкой отмены."
            : "Дождитесь завершения текущей операции агента.";
        return null;
    }
}
