using System.IO;
using System.Windows;
using Microsoft.Win32;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private async void ExportRecoveryKit_Click(object sender, RoutedEventArgs e)
    {
        if (!ExportRecoveryKitButton.IsEnabled || Copies.SelectedItem is not BackupCopyRow row
            || jobs.FirstOrDefault(job => job.Id == row.Record.JobId) is not { } job) return;
        var picker = new OpenFolderDialog { Title = "Выберите папку для комплекта восстановления" };
        if (picker.ShowDialog(this) != true) return;
        using var lease = AcquireEngineOrReport();
        if (lease is null) return;
        using var cancellation = new CancellationTokenSource();
        copyVerification = cancellation;
        UpdateRunControls();
        try
        {
            var progress = BeginCopyExportProgress();
            CopyVerificationStatus.Text = "Сохраняются и проверяются архивы комплекта…";
            var selectedCatalog = EffectiveCatalog;
            var path = await Task.Run(() => new FileRecoveryKit(backupStorage).ExportAsync(row.Record, job, selectedCatalog,
                picker.FolderName, token: cancellation.Token, progress: progress));
            CopyVerificationStatus.Text = "Комплект сохранён: " + path + (row.Record.Backup.ArchivePasswordKey is null
                ? "" : ". Для восстановления понадобится пароль архива.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { CopyVerificationStatus.Text = "Сохранение комплекта отменено."; }
        catch (Exception error)
        { CopyVerificationStatus.Text = "Комплект не сохранён: " + error.Message; }
        finally { EndCopyExportProgress(); copyVerification = null; UpdateRunControls(); }
    }

    private async void OpenRecoveryKit_Click(object sender, RoutedEventArgs e)
    {
        if (!OpenRecoveryKitButton.IsEnabled) return;
        var picker = new OpenFileDialog { Title = "Откройте описание комплекта восстановления",
            Filter = "Описание комплекта SP Backup|recovery.json", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        var folder = Path.GetDirectoryName(picker.FileName);
        if (folder is null || !string.Equals(Path.GetFileName(picker.FileName), FileRecoveryKit.ManifestName, StringComparison.OrdinalIgnoreCase))
        {
            CopyVerificationStatus.Text = "Выберите файл recovery.json из комплекта.";
            return;
        }
        using var lease = AcquireEngineOrReport();
        if (lease is null) return;
        using var cancellation = new CancellationTokenSource();
        copyVerification = cancellation;
        UpdateRunControls();
        try
        {
            var summary = await Task.Run(() => FileRecoveryKit.InspectAsync(folder, cancellation.Token));
            cancellation.Token.ThrowIfCancellationRequested();
            var dialog = new RecoveryKitWindow(summary) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.RestoreParent is not { } parent) return;
            var progress = BeginFileRestoreProgress();
            var workRoot = EffectiveWorkDirectory;
            var result = await Task.Run(() => FileRecoveryKit.RestoreAsync(folder, parent, workRoot, dialog.ArchivePassword,
                cancellation.Token, progress));
            CopyVerificationStatus.Text = $"Из комплекта восстановлено файлов: {result.Files}. Папка: {result.Directory}"
                + (result.SkippedPaths is { Count: > 0 } skipped ? $" · Файлов с пропущенной новой версией: {skipped.Count}." : "");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { CopyVerificationStatus.Text = "Восстановление комплекта отменено."; }
        catch (Exception error)
        { CopyVerificationStatus.Text = "Не удалось восстановить комплект: " + error.Message; }
        finally { EndCopyExportProgress(); copyVerification = null; UpdateRunControls(); }
    }
}
