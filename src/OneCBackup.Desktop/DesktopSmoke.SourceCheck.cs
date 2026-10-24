using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

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
