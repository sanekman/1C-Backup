using System.IO;
using System.Windows;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

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
        storageCheckJobId = job.Id;
        StorageCheckResults.Visibility = Visibility.Visible;
        StorageCheckResults.IsExpanded = false;
        StorageCheckResults.Header = "Проверяется источник: " + job.Name;
        StorageCheckDetails.Text = "Проверка доступа без создания копии. Платформа 1С не запускается.";
        Status.Text = StorageCheckResults.Header.ToString();
        UpdateRunControls();
        try
        {
            var checker = new SourceReadinessChecker(new BackupProcessRunner(), Path.Combine(DesktopPaths.DataDirectory, "work"));
            var report = await Task.Run(() => checker.CheckAsync(job, cancellation.Token));
            StorageCheckResults.Header = report.Cancelled ? "Проверка источника отменена: " + job.Name
                : report.Succeeded ? "Источник доступен: " + job.Name : "Источник не прошёл проверку: " + job.Name;
            StorageCheckDetails.Text = "Задание: " + job.Name + "\nИсточник: " + job.Source + "\n\n" + report.Details
                + "\n\nРезультат на " + DateTimeOffset.Now.ToString("dd.MM.yyyy HH:mm:ss") + ". Копии, история и правила хранения не изменялись.";
            StorageCheckResults.IsExpanded = !report.Succeeded;
            Status.Text = StorageCheckResults.Header.ToString();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            StorageCheckResults.Header = Status.Text = "Проверка источника отменена: " + job.Name;
            StorageCheckDetails.Text = "Проверка отменена до обращения к источнику.";
        }
        catch (Exception ex)
        {
            StorageCheckResults.Header = Status.Text = "Проверка источника не выполнена: " + job.Name;
            StorageCheckDetails.Text = ex.Message;
            StorageCheckResults.IsExpanded = true;
        }
        finally
        {
            checkingStorages = false; copyVerification = null;
            UpdateRunControls();
        }
    }
}
