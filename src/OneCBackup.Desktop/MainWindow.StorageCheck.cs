using System.IO;
using System.Windows;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

public partial class MainWindow
{
    private async void CheckStorage_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckStorageButton.IsEnabled || Jobs.SelectedItem is not BackupJob job) return;
        using var lease = AcquireEngineOrReport();
        if (lease is null) return;
        using var cancellation = new CancellationTokenSource();
        copyVerification = cancellation;
        checkingStorages = true;
        storageCheckJobId = job.Id;
        StorageCheckResults.Visibility = Visibility.Visible;
        StorageCheckResults.IsExpanded = false;
        StorageCheckResults.Header = "Проверяются хранилища: " + job.Name;
        StorageCheckDetails.Text = "Проверяется временная папка и запись, чтение и удаление тестового файла.";
        Status.Text = "Проверяются хранилища: " + job.Name;
        UpdateRunControls();
        try
        {
            var checker = new StorageReadinessChecker(backupStorage, Path.Combine(DesktopPaths.DataDirectory, "work"));
            var report = await Task.Run(() => checker.CheckAsync(job, cancellation.Token));
            var passed = report.Destinations.Count(result => result.Succeeded);
            var all = report.TemporaryError is null && passed == job.StorageJobs.Count && !report.Cancelled;
            StorageCheckResults.Header = report.Cancelled ? "Проверка отменена: " + job.Name
                : all ? "Все хранилища доступны: " + job.Name : $"Проверено хранилищ: {passed} из {job.StorageJobs.Count} — {job.Name}";
            StorageCheckDetails.Text = "Задание: " + job.Name + "\nВременная папка: " + report.TemporaryDirectory + "\n"
                + (report.TemporaryError is null ? "Запись и чтение временного файла проверены." : "Ошибка временной папки: " + report.TemporaryError)
                + "\n\n" + string.Join("\n\n", report.Destinations.Select(result => result.Destination + "\n"
                    + (result.Succeeded ? "Запись, чтение и удаление проверены." : result.Error)))
                + (report.Destinations.Count == 0 ? "\nХранилища не проверены." : "")
                + "\n\nЭто проверка доступа. Создание копии базы проверяется отдельным запуском.";
            StorageCheckResults.IsExpanded = !all;
            Status.Text = StorageCheckResults.Header.ToString();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            StorageCheckResults.Header = Status.Text = "Проверка отменена: " + job.Name;
            StorageCheckDetails.Text = "Проверка отменена до обращения к хранилищам.";
        }
        catch (Exception ex)
        {
            StorageCheckResults.Header = Status.Text = "Проверка не выполнена: " + job.Name;
            StorageCheckDetails.Text = ex.Message;
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
