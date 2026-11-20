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
            if (jobs.FirstOrDefault(job => job.Id == row.Record.JobId) is { } job
                && TryMultiStorageChain(row.Record, job, copyRows.Select(copy => copy.Record)) is not null)
            {
                copyRestoreChainError = null;
                CopyRestoreReadiness.Text = "Цепочку можно собрать из нескольких хранилищ.";
            }
        }
    }

    private async Task ShowRestoreCopyAsync(bool files)
    {
        if (!(files ? RestoreFilesButton.IsEnabled : ExtractOneCButton.IsEnabled) || Copies.SelectedItem is not BackupCopyRow row) return;
        var chain = files ? copyRestoreChain : new[] { row.Record };
        var job = files ? jobs.FirstOrDefault(job => job.Id == row.Record.JobId) : null;
        var historical = job is null ? null : ReadFormerStorage(row.Record, job);
        if (historical is not null)
        {
            var message = "Эта копия находится в прежнем хранилище:\n" + historical.DestinationDirectory
                + "\n\nSP Backup прочитает архивы с доступом текущего пользователя Windows. Настройки задания сохранятся.";
            if (MessageBox.Show(this, message, "Открыть прежнее хранилище", MessageBoxButton.OKCancel, MessageBoxImage.Information)
                != MessageBoxResult.OK) return;
            job = historical;
        }
        IReadOnlyList<BackupRecord>? alternate = null;
        preparingCopyRestore = true;
        UpdateRunControls();
        try
        {
            var records = files ? copyRows.Select(copy => copy.Record).ToArray() : [];
            var missing = await Task.Run(() => FirstMissingLocalArchive(chain));
            if (job is not null && (missing is not null || chain.Count == 0))
                alternate = await Task.Run(() => TryMultiStorageChain(row.Record, job, records, checkLocalAvailability: true));
            if (!CopiesView.IsVisible || Copies.SelectedItem is not BackupCopyRow current || current.Record != row.Record) return;
            if ((missing is not null || chain.Count == 0) && alternate is null)
            {
                CopyVerificationStatus.Text = missing is null ? "Восстановление невозможно. Не найдены все архивы цепочки."
                    : "Восстановление невозможно. Не найден архив: " + missing;
                return;
            }
        }
        finally
        {
            preparingCopyRestore = false;
            UpdateRunControls();
        }
        if (!(files ? RestoreFilesButton.IsEnabled : ExtractOneCButton.IsEnabled)) return;
        var dialog = new RestoreCopyWindow(row, files, alternate?.Count ?? chain.Count,
            alternate?.Select(record => record.StorageIdentity).Distinct().Count() ?? 0) { Owner = this };
        if (job is not null)
        {
            var restorer = new FileBackupRestorer(backupStorage, EffectiveCatalog, EffectiveWorkDirectory, new WindowsArchivePasswordStore());
            dialog.ReadContents = async (password, useOtherDestinations, token) =>
            {
                using var lease = AcquireEngineOrReport();
                if (lease is null) throw new InvalidOperationException(Status.Text);
                return await Task.Run(() => restorer.ReadContentsAsync(row.Record, job, token, password, useOtherDestinations), token);
            };
        }
        CopyRestoreDialogOpened?.Invoke(dialog);
        if (dialog.ShowDialog() != true || dialog.ParentDirectory is not { } parent
            || Copies.SelectedItem is not BackupCopyRow selected || selected.Record != row.Record) return;
        if (files) await RestoreSelectedFilesAsync(parent, dialog.PasswordOverride, dialog.SelectedPaths, dialog.UseOtherDestinations, historical);
        else await ExtractSelectedCopyAsync(parent, dialog.PasswordOverride);
    }

    private static BackupJob? ReadFormerStorage(BackupRecord point, BackupJob job)
    {
        try { return HistoricalFileCopy.CreateReadJob(point, job); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or NotSupportedException) { return null; }
    }

    private static IReadOnlyList<BackupRecord>? TryMultiStorageChain(BackupRecord point, BackupJob job,
        IEnumerable<BackupRecord> records, bool checkLocalAvailability = false)
    {
        try
        {
            var chain = FileBackupChains.ResolveAcrossStorages(point, job, records, checkLocalAvailability);
            return chain.Any(record => record.StorageIdentity != point.StorageIdentity)
                && (!checkLocalAvailability || FirstMissingLocalArchive(chain) is null) ? chain : null;
        }
        catch (InvalidDataException) { return null; }
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
