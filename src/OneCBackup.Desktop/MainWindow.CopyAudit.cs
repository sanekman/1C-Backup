using System.Windows;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

public partial class MainWindow
{
    private bool checkingLatestCopies;
    private async void CheckLatestCopies_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckLatestCopiesButton.IsEnabled || Jobs.SelectedItem is not BackupJob job) return;
        using var lease = AcquireEngineOrReport();
        if (lease is null) return;
        using var cancellation = new CancellationTokenSource();
        copyVerification = cancellation;
        checkingLatestCopies = checkingStorages = true;
        storageCheckJobId = job.Id;
        StorageCheckResults.Visibility = Visibility.Visible;
        StorageCheckResults.IsExpanded = false;
        StorageCheckResults.Header = Status.Text = "Проверяются последние копии: " + job.Name;
        StorageCheckDetails.Text = "Для каждого хранилища проверяется размер и SHA-256 последней зарегистрированной копии. База не копируется.";
        UpdateRunControls();
        try
        {
            var checker = new LatestBackupAudit(backupStorage, catalog);
            var report = await Task.Run(() => checker.CheckAsync(job, cancellation.Token));
            var passed = report.Destinations.Count(result => result.Status == BackupAuditStatus.Verified);
            var all = !report.Cancelled && passed == report.Destinations.Count;
            StorageCheckResults.Header = Status.Text = report.Cancelled ? "Проверка копий отменена: " + job.Name
                : all ? $"Все последние копии проверены: {passed} — {job.Name}"
                : $"Проверено последних копий: {passed} из {report.Destinations.Count} — {job.Name}";
            var details = new List<string> { "Задание: " + job.Name };
            details.AddRange(report.Destinations.OrderBy(result => result.Status == BackupAuditStatus.Verified).Select(result =>
                result.Destination + "\n" + (result.Status == BackupAuditStatus.Verified ? "Размер и SHA-256 совпадают."
                    : result.Status == BackupAuditStatus.Missing ? "Нет зарегистрированной копии."
                    : result.Status == BackupAuditStatus.Cancelled ? "Отменено или не проверено." : "Ошибка проверки.")
                + (result.Record is null ? "" : "\nКопия: " + result.Record.Backup.Location
                    + "\nСоздана: " + result.Record.CompletedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm zzz"))
                + (result.Error is null ? "" : "\n" + result.Error)));
            details.Add("Проверены только последние зарегистрированные копии в текущих хранилищах. Проверка не изменяет файлы, каталог, историю запусков или правила хранения. Совпадение SHA-256 не заменяет пробное восстановление базы.");
            StorageCheckDetails.Text = string.Join("\n\n", details);
            StorageCheckResults.IsExpanded = !all;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            StorageCheckResults.Header = Status.Text = "Проверка копий отменена: " + job.Name;
            StorageCheckDetails.Text = "Проверка отменена до чтения каталога копий.";
        }
        catch (Exception ex)
        {
            StorageCheckResults.Header = Status.Text = "Проверка копий не выполнена: " + job.Name;
            StorageCheckDetails.Text = ex.Message;
            StorageCheckResults.IsExpanded = true;
        }
        finally
        {
            checkingLatestCopies = checkingStorages = false;
            copyVerification = null;
            StorageCheckResults.Visibility = Jobs.SelectedItem is BackupJob selected && selected.Id == job.Id
                ? Visibility.Visible : Visibility.Collapsed;
            UpdateRunControls();
        }
    }
}
