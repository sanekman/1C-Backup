using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

public partial class MainWindow
{
    private static string FileSnapshotDetails(FileBackupSnapshot? snapshot) => snapshot is null ? "" : snapshot.IsFull
        ? "Файловая копия: полная. Для восстановления достаточно этого архива.\n"
        : $"Файловая копия: инкрементная. Для восстановления нужны полная основа и все изменения — {snapshot.Depth + 1} архивов в этом хранилище.\n";
    private void FileBackupMode_Changed(object sender, SelectionChangedEventArgs e) => UpdateFileBackupControls();
    private void UpdateFileBackupControls()
    {
        if (FileIncrementalOptions is null || FileBackupModePicker is null) return;
        FileIncrementalOptions.Visibility = FileBackupModePicker.SelectedValue is FileBackupMode.Incremental ? Visibility.Visible : Visibility.Collapsed;
        if (WizardHeading is null || WizardHint is null || JobEditor is null) return;
        var compactSource = wizardStep == 0 && Kind.SelectedItem is DatabaseKind.FilesAndFolders;
        WizardHeading.Visibility = compactSource ? Visibility.Collapsed : Visibility.Visible;
        WizardHint.Margin = new Thickness(0, 6, 0, compactSource || wizardStep == 3 ? 8 : 16);
        JobEditor.Margin = new Thickness(20, compactSource || wizardStep == 3 ? 8 : 16, 20, compactSource || wizardStep == 3 ? 8 : 16);
    }
    private async void RestoreFiles_Click(object sender, RoutedEventArgs e)
    {
        if (!RestoreFilesButton.IsEnabled || Copies.SelectedItem is not BackupCopyRow row) return;
        string? password = null;
        if (row.Record.Backup.ArchivePasswordKey is not null)
        {
            var passwordDialog = new ArchivePasswordWindow(restoring: true, restoreAction: "Восстановить файлы") { Owner = this };
            if (passwordDialog.ShowDialog() != true) return;
            password = passwordDialog.PasswordOverride;
        }
        var dialog = new OpenFolderDialog { Title = "Выберите папку: файлы будут восстановлены в новый подкаталог" };
        if (dialog.ShowDialog(this) == true) await RestoreSelectedFilesAsync(dialog.FolderName, password);
    }
    internal async Task RestoreSelectedFilesAsync(string parentDirectory, string? passwordOverride = null)
    {
        if (!RestoreFilesButton.IsEnabled || Copies.SelectedItem is not BackupCopyRow row) return;
        var job = jobs.FirstOrDefault(job => job.Id == row.Record.JobId); if (job is null) return;
        using var lease = AcquireEngineOrReport(); if (lease is null) { CopyVerificationStatus.Text = Status.Text; return; }
        using var cancellation = new CancellationTokenSource(); copyVerification = cancellation; UpdateRunControls();
        CopyVerificationStatus.Text = "Проверяется цепочка и восстанавливаются файлы в новую папку. Исходные данные не изменяются.";
        try
        {
            var result = await Task.Run(() => new FileBackupRestorer(backupStorage, catalog, Path.Combine(DesktopPaths.DataDirectory, "work"), new WindowsArchivePasswordStore())
                .RestoreAsync(row.Record, job, parentDirectory, cancellation.Token, passwordOverride));
            CopyVerificationStatus.Text = $"Восстановлено файлов: {result.Files}; архивов в цепочке: {result.Archives}. Папка: {result.Directory}";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { CopyVerificationStatus.Text = "Восстановление отменено. Исходные данные и копии сохранены."; }
        catch (Exception ex) { CopyVerificationStatus.Text = "Не удалось восстановить файлы: " + ex.Message; }
        finally { copyVerification = null; UpdateRunControls(); }
    }
}
