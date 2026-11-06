using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SPBackup.Core;

namespace SPBackup.Desktop;

internal static partial class DesktopSmoke
{
    private static async Task VerifyPersistentChecksAsync(MainWindow window, Action<string> screenshot)
    {
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var grid = Control<DataGrid>("Copies");
        var row = grid.Items.OfType<BackupCopyRow>().First(); grid.SelectedItem = row;
        var statePath = Path.Combine(DesktopPaths.DataDirectory, "verification-state.json");
        var state = new BackupVerificationState(statePath);
        var catalog = new BackupCatalog(Path.Combine(DesktopPaths.DataDirectory, "copies.json"));
        var history = new BackupRunHistory(Path.Combine(DesktopPaths.DataDirectory, "runs.json"));
        var beforeCatalog = JsonSerializer.Serialize(await catalog.ReadAsync());
        var beforeHistory = JsonSerializer.Serialize(await history.ReadAsync());
        async Task Refresh()
        {
            Control<Button>("RefreshCopiesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => window.SmokeReady, "Не обновились результаты проверок.");
            if ((grid.SelectedItem as BackupCopyRow)?.Record.Id != row.Record.Id)
                throw new InvalidOperationException("Обновление проверок потеряло выбранную копию.");
        }
        async Task Check(BackupVerificationOutcome outcome)
        {
            Control<Button>("VerifyCopyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => window.SmokeReady, "Ручная проверка не завершилась.");
            var entry = (await new BackupVerificationState(statePath).ReadAsync()).Single(check => check.BackupId == row.Record.Id);
            if (entry.Outcome != outcome || entry.Origin != BackupVerificationOrigin.Manual)
                throw new InvalidOperationException("Результат ручной проверки не сохранён.");
            await Refresh();
            if ((grid.SelectedItem as BackupCopyRow)?.Verification?.Outcome != outcome)
                throw new InvalidOperationException("Сохранённая проверка не появилась в таблице.");
        }
        await Check(BackupVerificationOutcome.Verified);
        var original = await File.ReadAllBytesAsync(row.Location);
        try
        {
            var corrupt = original.ToArray(); corrupt[0] ^= 1; await File.WriteAllBytesAsync(row.Location, corrupt);
            await Check(BackupVerificationOutcome.Failed);
            screenshot("copy-check-persisted-failure");
        }
        finally { await File.WriteAllBytesAsync(row.Location, original); }
        await Check(BackupVerificationOutcome.Verified);
        var savedState = await File.ReadAllTextAsync(statePath);
        try
        {
            await File.WriteAllTextAsync(statePath, "{\"Version\":1,\"Entries\":[{}]}");
            await Refresh();
            if (grid.Items.Count == 0 || (grid.SelectedItem as BackupCopyRow)?.CheckSummary != "Нет данных"
                || !Control<Button>("VerifyCopyButton").IsEnabled || !Control<TextBlock>("CopiesSummary").Text.Contains("недоступны"))
                throw new InvalidOperationException("Повреждённое состояние скрывает копии или показывает прежний успех.");
            screenshot("copy-check-state-unavailable");
        }
        finally { await File.WriteAllTextAsync(statePath, savedState); await Refresh(); }
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.ContextIdle);
        var scroll = FindDescendant<ScrollViewer>(grid);
        if (scroll is null || scroll.ExtentWidth > scroll.ViewportWidth + 1)
            throw new InvalidOperationException($"Таблица копий требует горизонтальной прокрутки: {scroll?.ExtentWidth}/{scroll?.ViewportWidth}.");
        var column = grid.Columns.Single(column => Equals(column.Header, "Последняя проверка"));
        var cell = column.GetCellContent(grid.SelectedItem);
        var text = cell is null ? null : FindDescendant<TextBlock>(cell);
        if (text is null || !text.Text.StartsWith("Успех") || !text.ToolTip.ToString()!.Contains("Последняя проверка:"))
            throw new InvalidOperationException("Столбец проверки не показывает сохранённый результат и дату.");
        if (Control<TextBlock>("SelectedCopyPath").Text != row.Location)
            throw new InvalidOperationException("Полный путь выбранной копии недоступен под таблицей.");
        if (JsonSerializer.Serialize(await catalog.ReadAsync()) != beforeCatalog || JsonSerializer.Serialize(await history.ReadAsync()) != beforeHistory)
            throw new InvalidOperationException("Состояние проверок изменило каталог или историю запусков.");
        screenshot("copy-check-persisted-compact");
    }
}
