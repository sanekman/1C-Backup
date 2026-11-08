using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SPBackup.Core;

namespace SPBackup.Desktop;

internal static partial class DesktopSmoke
{
    private static async Task VerifySourceCheckAsync(MainWindow window, Action<string> screenshot)
    {
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Click(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Click("JobsNavigation");
        var grid = Control<DataGrid>("Jobs");
        var job = grid.Items.OfType<BackupJob>().First(target => target.Kind == DatabaseKind.OneCFile && target.OneCMethod == OneCBackupMethod.FileArchive);
        grid.SelectedItem = job;
        var selectedSchedule = Control<Expander>("SelectedSchedulePanel");
        selectedSchedule.IsExpanded = true;
        if (selectedSchedule.Visibility != Visibility.Visible
            || !Control<TextBox>("SelectedScheduleDetails").Text.Contains("Задание: " + job.Name)
            || !Control<TextBox>("SelectedScheduleDetails").Text.Contains("Результат проверяйте в истории"))
            throw new InvalidOperationException("Прогноз выбранного задания не объясняет условия запуска.");
        selectedSchedule.IsExpanded = false;
        var source = Path.GetFullPath(job.Source);
        var file = string.Equals(Path.GetFileName(source), "1Cv8.1CD", StringComparison.OrdinalIgnoreCase) ? source : Path.Combine(source, "1Cv8.1CD");
        var sourceBytes = await File.ReadAllBytesAsync(file);
        var catalog = new BackupCatalog(Path.Combine(DesktopPaths.DataDirectory, "copies.json"));
        var history = new BackupRunHistory(Path.Combine(DesktopPaths.DataDirectory, "runs.json"));
        var store = new JobStore(Path.Combine(DesktopPaths.DataDirectory, "jobs.json"));
        var beforeCopies = JsonSerializer.Serialize(await catalog.ReadAsync());
        var beforeRuns = JsonSerializer.Serialize(await history.ReadAsync());
        var beforeJobs = JsonSerializer.Serialize(await store.LoadAsync());
        Click("CheckSourceButton");
        await WaitUntil(() => window.SmokeReady, "Проверка файла базы не завершилась.");
        if (!Control<Expander>("StorageCheckResults").Header.ToString()!.Contains("Источник доступен")
            || !Control<TextBox>("StorageCheckDetails").Text.Contains("эксклюзивный доступ")
            || Control<Expander>("StorageCheckResults").IsExpanded)
            throw new InvalidOperationException("Проверка источника не показывает доступ и ограничения.");
        var details = Control<TextBox>("StorageCheckDetails").Text;
        if (!details.Contains("Пользователь:") || !details.Contains("длительность:") || !details.Contains("Доступ службы Windows этим результатом не подтверждён"))
            throw new InvalidOperationException("Отчёт источника не объясняет пользователя, время или ограничения службы.");
        var originalItems = grid.ItemsSource;
        try
        {
            grid.SelectedItem = null;
            if (Control<Button>("CheckSourceButton").IsEnabled
                || !(Control<Button>("CheckSourceButton").ToolTip?.ToString()?.Contains("Выберите задание") ?? false))
                throw new InvalidOperationException("Нет понятной причины недоступности проверки без задания.");
            var changed = job with { Source = job.Source + "-changed" };
            grid.ItemsSource = ((IEnumerable<object>)originalItems).Select(item => item is BackupJob value && value.Id == job.Id ? changed : item).ToArray();
            grid.SelectedItem = changed;
            if (!Control<Expander>("StorageCheckResults").IsExpanded
                || !Control<TextBox>("StorageCheckDetails").Text.Contains("Настройки задания изменились после проверки"))
                throw new InvalidOperationException("Результат прежних настроек показан как актуальный.");
        }
        finally { grid.ItemsSource = originalItems; grid.SelectedItem = job; }
        if (!Control<Button>("RetryReadinessReportButton").IsEnabled || !Control<Button>("EditReadinessJobButton").IsEnabled)
            throw new InvalidOperationException("Отчёт не предлагает повторную проверку или исправление задания.");
        Click("RetryReadinessReportButton");
        await WaitUntil(() => window.SmokeReady, "Повторная проверка не завершилась.");
        if (Control<TextBox>("StorageCheckDetails").Text.Contains("Настройки задания изменились после проверки"))
            throw new InvalidOperationException("Новая проверка не сбросила предупреждение об устаревании.");
        if (!Control<TextBlock>("ReadinessReportState").Text.Contains("успешно"))
            throw new InvalidOperationException("Успешная повторная проверка не получила итоговый статус.");
        var reportPath = Path.Combine(Path.GetDirectoryName(DesktopPaths.DataDirectory)!, "check-report-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            window.ReadinessReportExportDialogOverride = dialog => { dialog.FileName = reportPath; return true; };
            Click("SaveReadinessReportButton");
            await WaitUntil(() => window.SmokeReady, "Сохранение отчёта не завершилось.");
            var exported = await File.ReadAllTextAsync(reportPath);
            if (!exported.Contains("Пользователь:") || !exported.Contains("эксклюзивный доступ"))
                throw new InvalidOperationException("Сохранённый отчёт потерял результат или контекст проверки.");
            Click("SaveReadinessReportButton");
            await WaitUntil(() => window.SmokeReady, "Проверка существующего отчёта не завершилась.");
            if (await File.ReadAllTextAsync(reportPath) != exported
                || !Control<TextBlock>("ReadinessReportActionStatus").Text.Contains("уже существует"))
                throw new InvalidOperationException("Существующий отчёт был заменён или ошибка не объяснена.");
            Click("CopyReadinessReportButton");
            if (Clipboard.GetText() != exported)
                throw new InvalidOperationException("Скопированный и сохранённый отчёты не совпали.");
            Click("ClearReadinessReportButton");
            if (Control<Expander>("StorageCheckResults").IsVisible || Control<TextBox>("StorageCheckDetails").Text.Length != 0
                || Control<Button>("RetryReadinessReportButton").IsEnabled)
                throw new InvalidOperationException("Очистка отчёта не скрыла прежний результат.");
        }
        finally { window.ReadinessReportExportDialogOverride = null; if (File.Exists(reportPath)) File.Delete(reportPath); }
        Click("CheckSourceButton");
        await WaitUntil(() => window.SmokeReady, "Проверка после очистки отчёта не завершилась.");
        window.Width = 900; window.Height = 580; window.UpdateLayout();
        screenshot("source-check-success-compact");
        await using (var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Click("CheckSourceButton");
            if (!window.SmokeReady && (Control<Button>("RunButton").IsEnabled || Control<Button>("CheckStorageButton").IsEnabled))
                throw new InvalidOperationException("Проверка источника не исключает параллельное копирование.");
            await WaitUntil(() => window.SmokeReady, "Проверка занятого файла базы не завершилась.");
            if (!Control<Expander>("StorageCheckResults").IsExpanded
                || !Control<TextBox>("StorageCheckDetails").Text.Contains("Закройте сеансы"))
                throw new InvalidOperationException("Занятый файл не показывает понятную ошибку источника.");
            window.UpdateLayout();
            var button = Control<Button>("CheckSourceButton");
            var bounds = button.TransformToAncestor(window).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
            if (!button.IsVisible || bounds.Bottom > window.ActualHeight - 10 || bounds.Left < 0 || bounds.Right > window.ActualWidth)
                throw new InvalidOperationException("Проверка источника недоступна в компактном окне.");
            screenshot("source-check-busy-compact");
        }
        var afterSourceBytes = await File.ReadAllBytesAsync(file);
        if (!sourceBytes.SequenceEqual(afterSourceBytes)
            || beforeCopies != JsonSerializer.Serialize(await catalog.ReadAsync())
            || beforeRuns != JsonSerializer.Serialize(await history.ReadAsync())
            || beforeJobs != JsonSerializer.Serialize(await store.LoadAsync()))
            throw new InvalidOperationException("Проверка источника изменила базу, копии, историю или задания.");
        Control<Expander>("StorageCheckResults").IsExpanded = false;
        window.Width = 1100; window.Height = 740;
    }
}
