using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

internal static partial class DesktopSmoke
{
    private static async Task VerifyLatestCopiesAuditAsync(MainWindow window, Action<string> screenshot)
    {
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        Control<Button>("JobsNavigation").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var grid = Control<DataGrid>("Jobs");
        var selected = grid.Items.OfType<BackupJob>().First();
        grid.SelectedItem = selected;
        var catalog = new BackupCatalog(Path.Combine(DesktopPaths.DataDirectory, "copies.json"));
        var history = new BackupRunHistory(Path.Combine(DesktopPaths.DataDirectory, "runs.json"));
        var records = await catalog.ReadAsync();
        var beforeCatalog = JsonSerializer.Serialize(records);
        var beforeHistory = JsonSerializer.Serialize(await history.ReadAsync());
        var existing = records.Where(record => record.JobId == selected.Id).MaxBy(record => record.CompletedUtc)!;
        async Task Check(string expected)
        {
            var button = Control<Button>("CheckLatestCopiesButton");
            if (!button.IsEnabled) throw new InvalidOperationException("Проверка последних копий недоступна.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => window.SmokeReady, "Проверка последних копий не завершилась.");
            window.UpdateLayout();
            var row = grid.ItemContainerGenerator.ContainerFromItem(selected) as DataGridRow;
            var buttonPosition = button.TranslatePoint(new Point(), window);
            if (row is null || row.ActualHeight < 20 || buttonPosition.Y + button.ActualHeight > window.ActualHeight)
                throw new InvalidOperationException("Проверка копий скрывает строку задания или кнопку в компактном окне.");
            var header = Control<Expander>("StorageCheckResults").Header.ToString()!;
            if (!header.Contains(expected) || !Control<TextBox>("StorageCheckDetails").Text.Contains("Нет зарегистрированной копии."))
                throw new InvalidOperationException("Проверка последних копий потеряла результат или отсутствующее хранилище: " + header);
        }
        // A busy source is irrelevant: audit must only read existing copies.
        using (var locked = new FileStream(Path.Combine(selected.Source, "1Cv8.1CD"), FileMode.Open, FileAccess.Read, FileShare.None))
            await Check("2 из 3");
        screenshot("latest-copies-audit-missing");
        var original = await File.ReadAllBytesAsync(existing.Backup.Location);
        try
        {
            var corrupt = original.ToArray();
            corrupt[0] ^= 1;
            await File.WriteAllBytesAsync(existing.Backup.Location, corrupt);
            await Check("1 из 3");
            var details = Control<TextBox>("StorageCheckDetails").Text;
            if (!details.Contains("Ошибка проверки.") || !details.Contains(existing.Backup.Location)
                || !details.Contains("не заменяет пробное восстановление"))
                throw new InvalidOperationException("Проверка копий не объясняет повреждение и пределы SHA-256.");
            screenshot("latest-copies-audit-corrupt");
        }
        finally { await File.WriteAllBytesAsync(existing.Backup.Location, original); }
        await Check("2 из 3");
        var restored = await File.ReadAllBytesAsync(existing.Backup.Location);
        if (JsonSerializer.Serialize(await catalog.ReadAsync()) != beforeCatalog
            || JsonSerializer.Serialize(await history.ReadAsync()) != beforeHistory
            || !original.SequenceEqual(restored))
            throw new InvalidOperationException("Проверка последних копий изменила каталог, историю или файл.");
        screenshot("latest-copies-audit-compact");
    }
}
