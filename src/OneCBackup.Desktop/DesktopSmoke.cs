using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

/// <summary>Opt-in Windows CI check, isolated from the user's real profile and databases.</summary>
internal static class DesktopSmoke
{
    public static void Finish(string output, bool success, string details)
    {
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "smoke-result.json"), JsonSerializer.Serialize(new { success, details }));
        Application.Current.Shutdown(success ? 0 : 1);
    }

    private static async Task WaitUntil(Func<bool> predicate, string operation)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException(operation);
            await Task.Delay(100);
        }
    }

    public static async Task RunAsync(string output)
    {
        try
        {
            await WaitUntil(() => Application.Current.MainWindow is MainWindow { SmokeReady: true }, "Окно не завершило загрузку.");
            var window = (MainWindow)Application.Current.MainWindow;
            T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
            void Click(string name)
            {
                var button = Control<Button>(name);
                if (!button.IsEnabled) throw new InvalidOperationException("Кнопка недоступна: " + name);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            var source = Path.Combine(DesktopPaths.DataDirectory, "fixture-base");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "1Cv8.1CD"), "CI fixture bytes; not a real 1C database");
            Control<TextBox>("JobName").Text = "Тестовая база 1С";
            Control<TextBox>("Source").Text = source;
            Control<TextBox>("Destination").Text = Path.Combine(DesktopPaths.DataDirectory, "copies");
            Control<ComboBox>("OneCMethodPicker").SelectedValue = OneCBackupMethod.FileArchive;
            Control<TextBox>("DailyAt").Text = "23:00, 09:00, 13:00";
            Click("SaveButton");
            await WaitUntil(() => Control<DataGrid>("Jobs").Items.Count == 1 && Control<Button>("RunButton").IsEnabled, "Расписание не сохранено.");
            var configured = (BackupJob)Control<DataGrid>("Jobs").Items[0];
            if (!configured.ScheduledTimes.SequenceEqual(new TimeOnly[] { new(9, 0), new(13, 0), new(23, 0) }))
                throw new InvalidOperationException("Несколько времён запуска не сохранены через интерфейс.");
            Control<ComboBox>("Schedule").SelectedValue = ScheduleKind.Manual;
            Click("SaveButton");
            await WaitUntil(() => window.SmokeReady && Control<DataGrid>("Jobs").SelectedItem is BackupJob { ScheduleKind: ScheduleKind.Manual }
                && Control<Button>("RunButton").IsEnabled, "Задание не сохранено.");
            Click("RunButton");
            await WaitUntil(() => Control<DataGrid>("RunHistory").Items.OfType<BackupRun>().Any(run => run.Status == BackupRunStatus.Succeeded)
                && window.SmokeReady, "Копия не создана через интерфейс.");
            // A locked source must produce a visible failure without creating another copy.
            using (var lockedSource = new FileStream(Path.Combine(source, "1Cv8.1CD"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Click("RunButton");
                await WaitUntil(() => Control<DataGrid>("RunHistory").Items.OfType<BackupRun>().Any(run => run.Status == BackupRunStatus.Failed)
                    && window.SmokeReady, "Ошибка занятого файла не появилась в истории.");
            }
            Click("HistoryNavigation");
            Control<ComboBox>("HistoryStatusFilter").SelectedIndex = 1;
            var filtered = Control<DataGrid>("RunHistory");
            if (filtered.Items.Count != 1 || filtered.Items[0] is not BackupRun { Status: BackupRunStatus.Failed })
                throw new InvalidOperationException("Фильтр ошибок показывает неверные записи.");
            filtered.SelectedIndex = 0;
            if (!Control<TextBox>("RunDetails").Text.Contains("Ошибка:"))
                throw new InvalidOperationException("Подробности ошибки недоступны.");
            var exportedPath = Path.Combine(output, "history-export.json");
            await window.ExportHistoryAsync(exportedPath);
            using (var exported = JsonDocument.Parse(File.ReadAllText(exportedPath)))
            {
                var runs = exported.RootElement.GetProperty("Runs");
                if (runs.GetArrayLength() != 1 || runs[0].GetProperty("Error").GetString() is not { Length: > 0 })
                    throw new InvalidOperationException("Экспорт не соответствует фильтру истории.");
            }
            try
            {
                await window.ExportHistoryAsync(Path.Combine(DesktopPaths.DataDirectory, "runs.json"));
                throw new InvalidOperationException("Экспорт разрешил перезаписать рабочий журнал.");
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("Выберите папку вне")) { }
            Control<TextBox>("HistorySearch").Text = "no-such-job-or-error-203a8b";
            if (filtered.Items.Count != 0 || Control<Button>("ExportHistoryButton").IsEnabled)
                throw new InvalidOperationException("Поиск в истории не скрывает неподходящие записи.");
            Control<TextBox>("HistorySearch").Text = "";
            Control<ComboBox>("HistoryStatusFilter").SelectedIndex = 0;
            foreach (var (navigation, name) in new[] { ("JobsNavigation", "jobs"), ("CopiesNavigation", "copies"),
                ("HistoryNavigation", "history"), ("AgentNavigation", "agent") })
            {
                Click(navigation);
                await Task.Delay(200);
                window.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight),
                    96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
            }
            Click("CopiesNavigation");
            await WaitUntil(() => Control<DataGrid>("Copies").Items.Count == 1, "Каталог копий не обновился.");
            Control<DataGrid>("Copies").SelectedIndex = 0;
            Click("VerifyCopyButton");
            await WaitUntil(() => Control<TextBlock>("CopyVerificationStatus").Text.StartsWith("Размер и SHA256 совпадают"), "Проверка копии не завершилась.");
            await VerifyPostgreSqlMonitoringAsync(window);
            Finish(output, true, "Несколько времён, ZIP, ошибка занятой базы, фильтры и экспорт истории, каталог, SHA256, навигация и импорт SQL-отчёта проверены на Windows.");
        }
        catch (Exception ex) { Finish(output, false, ex.ToString()); }
    }

    private static async Task VerifyPostgreSqlMonitoringAsync(MainWindow owner)
    {
        var directory = Path.Combine(DesktopPaths.DataDirectory, "sql-reports");
        Directory.CreateDirectory(directory);
        var observation = new ExternalBackupObservation("ci-pg-backup", "ci-cluster", "ci-database", "LogicalFull",
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow, "UTC", ExternalBackupOutcome.Completed,
            128, ["D:\\Backups\\fixture.dump"], "PgRestoreCatalogChecked");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        await File.WriteAllTextAsync(Path.Combine(directory, "fixture.json"), JsonSerializer.Serialize(observation, options));
        var settings = await new AgentSettingsStore(Path.Combine(DesktopPaths.DataDirectory, "agent.json")).LoadAsync();
        var monitorWindow = new ExternalMonitoringWindow(settings) { Owner = owner };
        monitorWindow.Show();
        try
        {
            await WaitUntil(() => monitorWindow.SmokeReady, "Мониторинг не загрузился.");
            T Control<T>(string name) where T : FrameworkElement => (T)monitorWindow.FindName(name);
            void Click(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Control<ComboBox>("SourceKind").SelectedValue = ExternalBackupSourceKind.PostgreSqlReports;
            Control<TextBox>("MonitorName").Text = "Тестовый монитор PostgreSQL";
            Control<TextBox>("ReportDirectory").Text = directory;
            Click("SaveMonitorButton");
            await WaitUntil(() => monitorWindow.SmokeReady && Control<ComboBox>("Sources").Items.Count == 1,
                "Источник мониторинга не сохранён.");
            Click("CollectMonitorButton");
            await WaitUntil(() => monitorWindow.SmokeReady && Control<DataGrid>("Reports").Items.Count == 1,
                "Отчёт PostgreSQL не импортирован.");
            Control<DataGrid>("Reports").SelectedIndex = 0;
            if (!Control<TextBox>("ReportDetails").Text.Contains("ci-cluster"))
                throw new InvalidOperationException("Подробности наблюдения SQL не отображаются.");
            Click("CollectMonitorButton");
            await WaitUntil(() => monitorWindow.SmokeReady, "Повторная проверка SQL не завершилась.");
            if (Control<DataGrid>("Reports").Items.Count != 1)
                throw new InvalidOperationException("Мониторинг дублирует внешний отчёт.");
        }
        finally { monitorWindow.Close(); }
    }
}
