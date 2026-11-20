using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private const string RestoreViewPreference = "desktop-restore-view.txt";

    private static bool ReadClassicRestoreView(string directory)
    {
        try { return File.ReadAllText(Path.Combine(directory, RestoreViewPreference)) == "classic"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private void ClassicRestoreView_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(DesktopPaths.DataDirectory, RestoreViewPreference);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, ClassicRestoreView.IsChecked == true ? "classic" : "current");
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ClassicRestoreView.IsChecked = ReadClassicRestoreView(DesktopPaths.DataDirectory);
            Status.Text = "Не удалось сохранить вид восстановления.";
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        UpdateRunControls();
    }

    private void RecoveryKits_Click(object sender, RoutedEventArgs e)
    {
        if (!RecoveryKitsButton.IsEnabled) return;
        var menu = new ContextMenu { PlacementTarget = RecoveryKitsButton, Placement = PlacementMode.Bottom };
        var open = new MenuItem { Header = "Открыть комплект…" };
        open.Click += OpenRecoveryKit_Click;
        var create = new MenuItem { Header = "Сохранить комплект выбранной копии…", IsEnabled = RestoreFilesButton.IsEnabled };
        create.Click += ExportRecoveryKit_Click;
        menu.Items.Add(open);
        menu.Items.Add(create);
        menu.IsOpen = true;
    }

    private async void ExportRecoveryKit_Click(object sender, RoutedEventArgs e)
    {
        if (!RestoreFilesButton.IsEnabled || Copies.SelectedItem is not BackupCopyRow row
            || jobs.FirstOrDefault(job => job.Id == row.Record.JobId) is not { } job) return;
        var historical = ReadFormerStorage(row.Record, job);
        if (historical is not null)
        {
            if (MessageBox.Show(this, $"Прочитать архивы из прежнего хранилища {historical.DestinationDirectory} с доступом текущего пользователя Windows?",
                "Сохранить комплект", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            job = historical;
        }
        var chain = copyRestoreChain;
        var records = copyRows.Select(copy => copy.Record).ToArray();
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
            var mixed = (chain.Count == 0 || await Task.Run(() => FirstMissingLocalArchive(chain), cancellation.Token) is not null)
                && await Task.Run(() => TryMultiStorageChain(row.Record, job, records, checkLocalAvailability: true), cancellation.Token) is not null;
            if (mixed && MessageBox.Show(this, "Для комплекта нужны архивы из нескольких хранилищ. Разрешить их чтение?",
                "Сохранить комплект", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
            {
                CopyVerificationStatus.Text = "Сохранение комплекта отменено.";
                return;
            }
            var path = await Task.Run(() => new FileRecoveryKit(backupStorage).ExportAsync(row.Record, job, selectedCatalog,
                picker.FolderName, mixed, cancellation.Token, progress));
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
        if (!RecoveryKitsButton.IsEnabled) return;
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
