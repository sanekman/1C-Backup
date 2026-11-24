using System.Windows;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private enum ReadinessKind { Source, Storages, Copies }
    private enum ReadinessOutcome { Success, Warning, Failed, Cancelled }
    private ReadinessKind? readinessKind;
    private ReadinessOutcome? readinessOutcome;
    private DateTimeOffset readinessStartedUtc;
    private StorageCheckIdentity? readinessWindowIdentity;
    private bool readinessViaAgent;
    private string? readinessJobFingerprint;
    private bool readinessReportStale;

    private void BeginReadinessReport(BackupJob job, ReadinessKind kind, bool viaAgent = false)
    {
        readinessKind = kind; readinessOutcome = null;
        readinessStartedUtc = DateTimeOffset.UtcNow;
        readinessWindowIdentity = null;
        try { readinessWindowIdentity = StorageCheckIdentity.Current(); }
        catch (Exception error) { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Warning, error); }
        readinessViaAgent = viaAgent;
        StorageCheckDetails.Clear();
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
        RetryReadinessReportButton.IsEnabled = hasResult && (readinessKind switch
        { ReadinessKind.Source => CheckSourceButton.IsEnabled, ReadinessKind.Storages => CheckStorageButton.IsEnabled,
            ReadinessKind.Copies => CheckLatestCopiesButton.IsEnabled, _ => false });
        RetryReadinessReportButton.ToolTip = readinessKind switch
        { ReadinessKind.Source => CheckSourceButton.ToolTip, ReadinessKind.Storages => CheckStorageButton.ToolTip,
            ReadinessKind.Copies => CheckLatestCopiesButton.ToolTip, _ => "Сначала выполните проверку." };
        EditReadinessJobButton.IsEnabled = hasResult && EditJobButton.IsEnabled;
        if (checkingStorages)
        {
            ReadinessReportState.Text = "Проверка выполняется. Итоговый отчёт ещё не готов.";
            return;
        }
        var selected = Jobs.SelectedItem as BackupJob;
        StorageCheckResults.Visibility = selected?.Id == storageCheckJobId && selected is not null
            ? Visibility.Visible : Visibility.Collapsed;
        ReadinessReportState.Text = readinessReportStale ? "Настройки изменились — повторите проверку."
            : (readinessOutcome switch { ReadinessOutcome.Success => "Проверка завершена успешно.",
                ReadinessOutcome.Warning => "Проверка завершена с замечаниями.", ReadinessOutcome.Failed => "Проверка завершена с ошибкой.",
                ReadinessOutcome.Cancelled => "Проверка отменена: весь объём не подтверждён.", _ => "Итог проверки не получен." })
                + " Результат относится к моменту проверки.";
        if (selected is null || selected.Id != storageCheckJobId || readinessJobFingerprint is null || readinessReportStale) return;
        if (LocalStorageCheckRequest.Fingerprint(selected) == readinessJobFingerprint) return;
        readinessReportStale = true;
        ReadinessReportState.Text = "Настройки изменились — повторите проверку.";
        StorageCheckResults.Header = "Результат относится к прежним настройкам: " + selected.Name;
        StorageCheckDetails.Text = "Настройки задания изменились после проверки. Выполните проверку заново перед использованием результата.\n\n" + StorageCheckDetails.Text;
        StorageCheckResults.IsExpanded = true;
    }

    private string ReadinessFailureDetails(BackupJob job, string message) => "Задание: " + job.Name
        + "\n" + (readinessViaAgent ? "Запрос направлялся агенту; итоговый отчёт исполнителя не получен. " : "Проверка из окна программы. ")
        + "Пользователь окна: " + (readinessWindowIdentity?.Account ?? "не определён")
        + "\n" + ReadinessTime(readinessStartedUtc, DateTimeOffset.UtcNow) + "\n\n" + message
        + "\n\nПроверьте параметры задания и повторите проверку. При непонятной ошибке сохраните диагностику в настройках.";

    private void RetryReadinessReport_Click(object sender, RoutedEventArgs e)
    {
        if (!RetryReadinessReportButton.IsEnabled) return;
        switch (readinessKind)
        {
            case ReadinessKind.Source: CheckSource_Click(sender, e); break;
            case ReadinessKind.Storages: CheckStorage_Click(sender, e); break;
            case ReadinessKind.Copies: CheckLatestCopies_Click(sender, e); break;
        }
    }

    private void EditReadinessJob_Click(object sender, RoutedEventArgs e)
    { if (EditReadinessJobButton.IsEnabled) EditJob_Click(sender, e); }

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
