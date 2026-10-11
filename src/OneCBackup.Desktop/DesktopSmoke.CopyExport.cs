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
            }
            finally { await File.WriteAllBytesAsync(row.Location, original); }
            window.CopyExportDialogOverride = _ => false;
            Control<Button>("ExportCopyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!window.SmokeReady) throw new InvalidOperationException("Отмена выбора файла запустила операцию.");
            if (JsonSerializer.Serialize(await catalog.ReadAsync()) != beforeCatalog
                || JsonSerializer.Serialize(await history.ReadAsync()) != beforeHistory)
                throw new InvalidOperationException("Сохранение выбранной копии изменило каталог или историю.");
            screenshot("copy-export-compact");
        }
        finally { window.CopyExportDialogOverride = null; }
    }
}
