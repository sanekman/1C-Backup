using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SPBackup.Core;

namespace SPBackup.Desktop;

internal static partial class DesktopSmoke
{
    private static async Task VerifyDiagnosticsAsync(MainWindow window, Action<string> screenshot)
    {
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Click(string name)
        {
            window.UpdateLayout(); var button = Control<Button>(name);
            if (!button.IsEnabled || !button.IsVisible) throw new InvalidOperationException("Диагностика недоступна: " + name);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
        }
        await WaitUntil(() => window.SmokeReady && Control<Button>("ExportDiagnosticsButton").IsEnabled, "Выгрузка диагностики не готова.");
        var store = new JobStore(Path.Combine(DesktopPaths.DataDirectory, "jobs.json"));
        var history = new BackupRunHistory(Path.Combine(DesktopPaths.DataDirectory, "runs.json"));
        var catalog = new BackupCatalog(Path.Combine(DesktopPaths.DataDirectory, "copies.json"));
        var beforeJobs = JsonSerializer.Serialize(await store.LoadAsync()); var beforeRuns = JsonSerializer.Serialize(await history.ReadAsync());
        var beforeCopies = JsonSerializer.Serialize(await catalog.ReadAsync());
        Click("AgentNavigation"); Click("HelpSettingsButton"); Control<ScrollViewer>("SettingsContentScroll").ScrollToTop(); window.Width = 900; window.Height = 580; window.UpdateLayout();
        var button = Control<Button>("ExportDiagnosticsButton"); var scroll = Control<ScrollViewer>("SettingsContentScroll");
        var point = button.TranslatePoint(new Point(), scroll);
        if (point.Y < 0 || point.Y + button.ActualHeight > scroll.ActualHeight + 1) throw new InvalidOperationException("Кнопка диагностики скрыта в компактных настройках.");
        screenshot("diagnostics-settings-compact");
        var destination = Path.Combine(Path.GetDirectoryName(DesktopPaths.DataDirectory)!, "diagnostics-fixture.zip");
        window.DiagnosticExportDialogOverride = dialog => { dialog.FileName = destination; return true; };
        try
        {
            Click("ExportDiagnosticsButton"); await WaitUntil(() => window.SmokeReady, "Выгрузка диагностики не завершилась.");
            if (!Control<TextBlock>("DiagnosticsStatus").Text.Contains("Диагностика сохранена")) throw new InvalidOperationException("Диагностика не сохранилась.");
            using (var zip = ZipFile.OpenRead(destination))
            {
                if (!zip.Entries.Any(entry => entry.FullName.StartsWith("logs/")) || zip.GetEntry("runs-summary.json") is null || zip.GetEntry("READ-ME.txt") is null)
                    throw new InvalidOperationException("В диагностике отсутствуют логи или сведения о запусках.");
                using var infoReader = new StreamReader(zip.GetEntry("support-info.json")!.Open());
                using var info = JsonDocument.Parse(await infoReader.ReadToEndAsync());
                if (info.RootElement.GetProperty("Version").GetString() != ApplicationBuildInfo.Version
                    || info.RootElement.GetProperty("Revision").GetString() != ApplicationBuildInfo.Revision)
                    throw new InvalidOperationException("Диагностика потеряла версию или коммит программы.");
                foreach (var entry in zip.Entries)
                {
                    if (entry.Name is "jobs.json" or "agent.json" or "copies.json" or "runs.json") throw new InvalidOperationException("Диагностика копирует сырой профиль.");
                    using var reader = new StreamReader(entry.Open()); var text = await reader.ReadToEndAsync();
                    if (text.Contains("fixture-webdav-app-password") || text.Contains("fixture-pg-saved-password") || text.Contains(DesktopPaths.DataDirectory))
                        throw new InvalidOperationException("Диагностика раскрывает секрет или путь профиля.");
                }
            }
            var bytes = await File.ReadAllBytesAsync(destination);
            Click("ExportDiagnosticsButton"); await WaitUntil(() => window.SmokeReady, "Повторный экспорт не завершился.");
            var afterBytes = await File.ReadAllBytesAsync(destination);
            if (!Control<TextBlock>("DiagnosticsStatus").Text.Contains("Не удалось") || !bytes.SequenceEqual(afterBytes))
                throw new InvalidOperationException("Повторная диагностика перезаписала существующий ZIP.");
            screenshot("diagnostics-existing-file-refused");
            if (beforeJobs != JsonSerializer.Serialize(await store.LoadAsync()) || beforeRuns != JsonSerializer.Serialize(await history.ReadAsync())
                || beforeCopies != JsonSerializer.Serialize(await catalog.ReadAsync())) throw new InvalidOperationException("Диагностика изменила задания / историю / копии.");
        }
        finally { window.DiagnosticExportDialogOverride = null; }
    }
}
