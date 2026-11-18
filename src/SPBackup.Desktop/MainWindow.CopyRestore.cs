using System.IO;
using System.Windows;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private IReadOnlyList<BackupRecord> copyRestoreChain = [];
    private string? copyRestoreChainError;
    private bool preparingCopyRestore;
    internal Action<RestoreCopyWindow>? CopyRestoreDialogOpened { get; set; }

    private void UpdateCopyRestoreChain(BackupCopyRow? row)
    {
        copyRestoreChain = [];
        copyRestoreChainError = null;
        CopyRestoreReadiness.Visibility = row?.Record.Backup.FileSnapshot is not null ? Visibility.Visible : Visibility.Collapsed;
        CopyRestoreReadiness.Text = "";
        if (row?.Record.Backup.FileSnapshot is null) return;
        try
        {
            copyRestoreChain = FileBackupChains.Resolve(row.Record, copyRows.Select(copy => copy.Record));
            CopyRestoreReadiness.Text = $"В каталоге найдены все архивы ({copyRestoreChain.Count}). Для проверки файлов нажмите «Проверить целостность».";
        }
        catch (InvalidDataException error)
        {
            copyRestoreChainError = error.Message;
            CopyRestoreReadiness.Text = "Цепочка недоступна. " + error.Message;
        }
    }

    private async Task ShowRestoreCopyAsync(bool files)
    {
        if (!(files ? RestoreFilesButton.IsEnabled : ExtractOneCButton.IsEnabled) || Copies.SelectedItem is not BackupCopyRow row) return;
        var chain = files ? copyRestoreChain : new[] { row.Record };
        preparingCopyRestore = true;
        UpdateRunControls();
        try
        {
            var missing = await Task.Run(() => FirstMissingLocalArchive(chain));
            if (!CopiesView.IsVisible || Copies.SelectedItem is not BackupCopyRow current || current.Record != row.Record) return;
            if (missing is not null)
            {
                CopyVerificationStatus.Text = "Восстановление невозможно. Не найден архив: " + missing;
                return;
            }
        }
        finally
        {
            preparingCopyRestore = false;
            UpdateRunControls();
        }
        if (!(files ? RestoreFilesButton.IsEnabled : ExtractOneCButton.IsEnabled)) return;
        var dialog = new RestoreCopyWindow(row, files, chain.Count) { Owner = this };
        CopyRestoreDialogOpened?.Invoke(dialog);
        if (dialog.ShowDialog() != true || dialog.ParentDirectory is not { } parent
            || Copies.SelectedItem is not BackupCopyRow selected || selected.Record != row.Record) return;
        if (files) await RestoreSelectedFilesAsync(parent, dialog.PasswordOverride);
        else await ExtractSelectedCopyAsync(parent, dialog.PasswordOverride);
    }

    private static string? FirstMissingLocalArchive(IReadOnlyList<BackupRecord> chain)
    {
        foreach (var record in chain)
        {
            var path = record.Backup.Location;
            if (!record.StorageIdentity.StartsWith("LocalDirectory:", StringComparison.Ordinal)
                || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) continue;
            try { if (File.GetAttributes(path).HasFlag(FileAttributes.Directory)) return path; }
            catch (FileNotFoundException) { return path; }
            catch (DirectoryNotFoundException) { return path; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { /* The restore operation reports errors from opening and validating the archive. */ }
        }
        return null;
    }
}
