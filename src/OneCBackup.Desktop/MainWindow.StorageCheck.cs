using System.IO;
using System.Windows;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

public partial class MainWindow
{
    private void JobsView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (StorageCheckDetails is not null) StorageCheckDetails.MaxHeight = ActualHeight < 650 ? 60 : 130;
    }

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
            var details = new List<string> { "Задание: " + job.Name };
            if (report.TemporaryError is not null)
                details.Add("Ошибка временной папки: " + report.TemporaryDirectory + "\n" + report.TemporaryError);
            details.AddRange(report.Destinations.Where(result => !result.Succeeded)
                .Select(result => result.Destination + "\nОшибка: " + result.Error));
            if (report.TemporaryError is null)
                details.Add("Временная папка: " + report.TemporaryDirectory + "\nЗапись и чтение временного файла проверены.");
            details.AddRange(report.Destinations.Where(result => result.Succeeded)
                .Select(result => result.Destination + "\nЗапись, чтение и удаление проверены."));
            if (report.Destinations.Count == 0) details.Add("Хранилища не проверены.");
            details.Add("Это проверка доступа. Создание копии базы проверяется отдельным запуском.");
            StorageCheckDetails.Text = string.Join("\n\n", details);
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
