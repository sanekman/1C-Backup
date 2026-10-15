using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

internal static partial class DesktopSmoke
{
    private static async Task VerifyCopyExportAsync(MainWindow window, Action<string> screenshot)
    {
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        Control<Button>("CopiesNavigation").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => window.SmokeReady, "Каталог не обновился перед сохранением копии.");
        Control<Button>("ResetCopyFiltersButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var grid = Control<DataGrid>("Copies");
        var row = grid.Items.OfType<BackupCopyRow>().First();
        grid.SelectedItem = row;
        var catalog = new BackupCatalog(Path.Combine(DesktopPaths.DataDirectory, "copies.json"));
        var history = new BackupRunHistory(Path.Combine(DesktopPaths.DataDirectory, "runs.json"));
        var beforeCatalog = JsonSerializer.Serialize(await catalog.ReadAsync());
        var beforeHistory = JsonSerializer.Serialize(await history.ReadAsync());
        var original = await File.ReadAllBytesAsync(row.Location);
        var destination = Path.Combine(DesktopPaths.DataDirectory, "saved-copy.zip");
        var observedPhases = new HashSet<BackupExportPhase>();
        var cancellationRequested = false;
        window.CopyExportProgressObserved = value =>
        {
            var first = observedPhases.Add(value.Phase);
            if (Control<System.Windows.Controls.Border>("CopyExportProgressPanel").Visibility != Visibility.Visible)
                throw new InvalidOperationException("Ход сохранения не виден во время операции.");
            var label = Control<TextBlock>("CopyExportProgressLabel").Text;
            if (value.Phase == BackupExportPhase.Verifying && !label.Contains("SHA-256")
                && !(cancellationRequested && label.StartsWith("Отмена:")))
                throw new InvalidOperationException("Проверка SHA-256 не выделена в отдельный этап.");
            if (first && value.Phase is BackupExportPhase.Transferring or BackupExportPhase.Verifying)
                screenshot(value.Phase == BackupExportPhase.Transferring ? "copy-export-progress-transfer" : "copy-export-progress-sha256");
        };
        async Task Export(string path, string expected)
        {
            var button = Control<Button>("ExportCopyButton");
            if (!button.IsEnabled) throw new InvalidOperationException("Сохранение выбранной копии недоступно.");
            window.CopyExportDialogOverride = dialog => { dialog.FileName = path; return true; };
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => window.SmokeReady, "Сохранение выбранной копии не завершилось.");
            if (!Control<TextBlock>("CopyVerificationStatus").Text.Contains(expected))
                throw new InvalidOperationException("Неверный результат сохранения копии: " + Control<TextBlock>("CopyVerificationStatus").Text);
        }
        try
        {
            await Export(destination, "Копия сохранена");
            if (!observedPhases.Contains(BackupExportPhase.Transferring) || !observedPhases.Contains(BackupExportPhase.Verifying)
                || Control<System.Windows.Controls.Border>("CopyExportProgressPanel").Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Передача и SHA-256 не показаны или индикатор остался после завершения.");
            var saved = await File.ReadAllBytesAsync(destination);
            if (!saved.SequenceEqual(original) || !Control<TextBlock>("CopyVerificationStatus").Text.Contains("Архив остаётся зашифрованным"))
                throw new InvalidOperationException("Сохранение изменило байты зашифрованного архива или потеряло пояснение пароля.");
            screenshot("copy-export-encrypted");
            await Export(destination, "уже существует");
            var stillSaved = await File.ReadAllBytesAsync(destination);
            if (!stillSaved.SequenceEqual(original)) throw new InvalidOperationException("Сохранение перезаписало существующий файл.");
            var invalid = Path.Combine(DesktopPaths.DataDirectory, "bad-copy.zip");
            try
            {
                var corrupt = original.ToArray(); corrupt[0] ^= 1;
                await File.WriteAllBytesAsync(row.Location, corrupt);
                await Export(invalid, "не совпадает с каталогом");
                if (File.Exists(invalid) || Directory.GetFiles(DesktopPaths.DataDirectory, ".onec-export-*.partial").Length != 0)
                    throw new InvalidOperationException("Повреждённая копия опубликована или оставила временный файл.");
                screenshot("copy-export-corrupt");
                if (!Control<TextBlock>("Status").Text.Contains("не совпадает с каталогом"))
                    throw new InvalidOperationException("Нижняя строка оставила прежний успех после повреждённой копии.");
            }
            finally { await File.WriteAllBytesAsync(row.Location, original); }
            var cancelledFile = Path.Combine(DesktopPaths.DataDirectory, "cancelled-export.zip");
            window.CopyExportDialogOverride = dialog => { dialog.FileName = cancelledFile; return true; };
            Control<Button>("ExportCopyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            cancellationRequested = true;
            Control<Button>("CancelCopyVerificationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => window.SmokeReady, "Отмена сохранения не завершилась.");
            var cancelled = Control<TextBlock>("Status").Text.Contains("Сохранение отменено");
            var completedBeforeCancel = Control<TextBlock>("Status").Text.Contains("Копия сохранена");
            // A tiny file may be atomically published before the cancel click reaches the worker.
            if ((!cancelled && !completedBeforeCancel) || (cancelled && File.Exists(cancelledFile))
                || (completedBeforeCancel && (!File.Exists(cancelledFile) || !(await File.ReadAllBytesAsync(cancelledFile)).SequenceEqual(original)))
                || Directory.GetFiles(DesktopPaths.DataDirectory, ".onec-export-*.partial").Length != 0
                || Control<System.Windows.Controls.Border>("CopyExportProgressPanel").Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Отмена сохранения потеряла результат операции или оставила временный файл / индикатор.");
            window.CopyExportDialogOverride = _ => false;
            Control<Button>("ExportCopyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!window.SmokeReady) throw new InvalidOperationException("Отмена выбора файла запустила операцию.");
            if (JsonSerializer.Serialize(await catalog.ReadAsync()) != beforeCatalog
                || JsonSerializer.Serialize(await history.ReadAsync()) != beforeHistory)
                throw new InvalidOperationException("Сохранение выбранной копии изменило каталог или историю.");
            screenshot("copy-export-compact");
        }
        finally { window.CopyExportDialogOverride = null; window.CopyExportProgressObserved = null; }
    }
}
