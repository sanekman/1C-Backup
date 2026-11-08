using System.IO;
using System.Windows;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private async void CheckSource_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckSourceButton.IsEnabled || Jobs.SelectedItem is not BackupJob job) return;
        using var lease = AcquireEngineOrReport();
        if (lease is null) return;
        using var cancellation = new CancellationTokenSource();
        copyVerification = cancellation;
        checkingStorages = true;
        BeginReadinessReport(job, ReadinessKind.Source);
        StorageCheckResults.Visibility = Visibility.Visible;
        StorageCheckResults.IsExpanded = false;
        StorageCheckResults.Header = "Проверяется источник: " + job.Name;
        StorageCheckDetails.Text = "Проверка выполняется из окна программы, от его пользователя. Копия не создаётся, платформа 1С не запускается.";
        Status.Text = StorageCheckResults.Header.ToString();
        UpdateRunControls();
        try
        {
            var checker = new SourceReadinessChecker(new BackupProcessRunner(), Path.Combine(DesktopPaths.DataDirectory, "work"));
            var report = await Task.Run(() => checker.CheckAsync(job, cancellation.Token));
            readinessOutcome = report.Cancelled ? ReadinessOutcome.Cancelled : report.Succeeded ? ReadinessOutcome.Success : ReadinessOutcome.Failed;
            StorageCheckResults.Header = report.Cancelled ? "Проверка источника отменена: " + job.Name
                : report.Succeeded ? "Источник доступен: " + job.Name : "Источник не прошёл проверку: " + job.Name;
            StorageCheckDetails.Text = "Задание: " + job.Name + "\nИсточник: " + job.Source
                + "\nПроверка из окна программы. Пользователь: " + (report.ExecutionIdentity?.Account ?? "не определён")
                + "\n" + ReadinessTime(report.StartedUtc, report.FinishedUtc)
                + "\n\n" + report.Details
                + "\n\nДоступ службы Windows этим результатом не подтверждён. Первую копию выполните через того исполнителя, который будет работать автоматически."
                + "\n\nРезультат на " + DateTimeOffset.Now.ToString("dd.MM.yyyy HH:mm:ss") + ". Копии, история и правила хранения не изменялись.";
            StorageCheckResults.IsExpanded = !report.Succeeded;
            Status.Text = StorageCheckResults.Header.ToString();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            readinessOutcome = ReadinessOutcome.Cancelled;
            StorageCheckResults.IsExpanded = true;
            StorageCheckResults.Header = Status.Text = "Проверка источника отменена: " + job.Name;
            StorageCheckDetails.Text = ReadinessFailureDetails(job, "Проверка отменена. Часть источника могла быть проверена; доступ ко всему источнику не подтверждён.");
        }
        catch (Exception ex)
        { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex);
            StorageCheckResults.Header = Status.Text = "Проверка источника не выполнена: " + job.Name;
            readinessOutcome = ReadinessOutcome.Failed;
            StorageCheckDetails.Text = ReadinessFailureDetails(job, ex.Message);
            StorageCheckResults.IsExpanded = true;
        }
        finally
        {
            checkingStorages = false; copyVerification = null;
            StorageCheckResults.Visibility = Jobs.SelectedItem is BackupJob selected && selected.Id == job.Id
                ? Visibility.Visible : Visibility.Collapsed;
            UpdateRunControls();
        }
    }
}
