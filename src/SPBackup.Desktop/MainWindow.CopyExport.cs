using System.IO;
using System.Windows;
using Microsoft.Win32;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    internal Func<SaveFileDialog, bool?>? CopyExportDialogOverride { get; set; }

    private async void ExportCopy_Click(object sender, RoutedEventArgs e)
    {
        if (!ExportCopyButton.IsEnabled || Copies.SelectedItem is not BackupCopyRow row) return;
        var job = jobs.FirstOrDefault(job => job.Id == row.Record.JobId);
        if (job is null) { CopyVerificationStatus.Text = "Настройки задания недоступны. Сохранить копию нельзя."; return; }
        try
        {
            BackupCopyVerifier.GetStorageJob(row.Record, job);
            var name = row.Record.Backup.OriginalFileName ?? (row.StorageCategory == "ftp" && Uri.TryCreate(row.Location, UriKind.Absolute, out var uri)
                ? Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath)) : Path.GetFileName(row.Location));
            var dialog = new SaveFileDialog { Title = row.Record.Backup.FileSnapshot is { IsFull: false }
                ? "Сохранить инкрементный архив — для восстановления нужна вся цепочка" : "Сохранить резервную копию на компьютер", FileName = name,
                Filter = "Все файлы (*.*)|*.*", DefaultExt = Path.GetExtension(name), AddExtension = true, OverwritePrompt = false, CheckPathExists = true };
            var chosen = CopyExportDialogOverride is null ? dialog.ShowDialog(this) : CopyExportDialogOverride(dialog);
            if (chosen != true) return;
            using var lease = AcquireEngineOrReport();
            if (lease is null) { CopyVerificationStatus.Text = Status.Text; return; }
            using var cancellation = new CancellationTokenSource();
            copyVerification = cancellation;
            UpdateRunControls();
            var progress = BeginCopyExportProgress();
            CopyVerificationStatus.Text = Status.Text = "Сохраняется копия с проверкой размера и SHA-256: " + row.DisplayLocation;
            try
            {
                var exported = await Task.Run(() => new BackupCopyExporter(backupStorage).ExportAsync(row.Record, job,
                    dialog.FileName, cancellation.Token, progress));
                CopyVerificationStatus.Text = "Копия сохранена, размер и SHA-256 совпадают: " + exported.Path
                    + (row.Record.Backup.ArchivePasswordKey is not null ? ". Архив остаётся зашифрованным; для распаковки нужен пароль." : "")
                    + (row.Record.Backup.FileSnapshot is { IsFull: false } snapshot
                        ? $". Это архив изменений. Для восстановления нужна вся цепочка с полной основой (архивов: {snapshot.Depth + 1})."
                        : row.Record.Backup.FileSnapshot is not null ? ". Проверьте содержимое кнопкой «Восстановить файлы…»."
                        : ". Проверка не заменяет пробное восстановление базы.");
                Status.Text = "Копия сохранена: " + exported.Path;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            { CopyVerificationStatus.Text = Status.Text = "Сохранение отменено. Готовый файл не создан."; }
            catch (Exception ex) { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex); CopyVerificationStatus.Text = Status.Text = "Копия не сохранена: " + ex.Message; }
            finally { EndCopyExportProgress(); copyVerification = null; UpdateRunControls(); }
        }
        catch (Exception ex) { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex); CopyVerificationStatus.Text = Status.Text = "Копия не сохранена: " + ex.Message; }
    }
}
