using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

internal static partial class DesktopSmoke
{
    private static async Task VerifyMonitoringFiltersAsync(ExternalMonitoringWindow window, AgentSettings settings,
        ExternalBackupObservation observation, Action<string, Window> screenshot)
    {
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Click(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var first = (ExternalBackupMonitor)Control<ComboBox>("Sources").SelectedValue;
        Click("NewMonitorButton");
        Control<TextBox>("MonitorName").Text = first.Name;
        Control<TextBox>("ReportDirectory").Text = first.ReportDirectory;
        Control<CheckBox>("MonitorEnabled").IsChecked = false;
        Click("SaveMonitorButton");
        await WaitUntil(() => window.SmokeReady && Control<ComboBox>("Sources").Items.Count == 2, "Второй источник не сохранился.");
        var second = (ExternalBackupMonitor)Control<ComboBox>("Sources").SelectedValue;
        window.UpdateLayout();
        var sourcePicker = Control<ComboBox>("Sources");
        sourcePicker.ApplyTemplate();
        var selectedSourcePresenter = (ContentPresenter)sourcePicker.Template.FindName("SelectionPresenter", sourcePicker);
        var sourceText = FindDescendant<TextBlock>(selectedSourcePresenter);
        if (sourceText is null || !sourceText.Text.StartsWith(second.Id.ToString("N")[..8]))
            throw new InvalidOperationException("Список ручного опроса не различает одинаково названные источники.");
        var outbox = new ExternalBackupOutbox(Path.Combine(DesktopPaths.DataDirectory, "external-backups.json"));
        var failure = observation with { SourceKey = "ci-pg-failure", Outcome = ExternalBackupOutcome.Failed,
            Evidence = "PostgreSqlScriptFailure", Error = "Нет свободного места", SizeBytes = null };
        await outbox.ObserveAsync(settings, first, [failure]);
        await outbox.ObserveAsync(settings, second, [observation]);
        await outbox.ObserveAsync(settings with { AgentId = Guid.NewGuid() }, second, [observation with { SourceKey = "foreign-agent" }]);
        var before = JsonSerializer.Serialize(await outbox.ReadAsync());
        var monitorStore = new ExternalBackupMonitorStore(Path.Combine(DesktopPaths.DataDirectory, "external-monitors.json"));
        var beforeMonitors = JsonSerializer.Serialize(await monitorStore.LoadAsync());
        Click("RefreshReportsButton");
        await WaitUntil(() => window.SmokeReady, "Наблюдения не обновились.");
        var grid = Control<DataGrid>("Reports");
        if (grid.Items.Count != 3) throw new InvalidOperationException("Мониторинг показывает наблюдение другого агента или теряет свои.");
        var sources = Control<ComboBox>("ReportSourceFilter");
        var labels = sources.Items.Cast<object>().Skip(1).Select(choice => (string)choice.GetType().GetProperty("Label")!.GetValue(choice)!).ToArray();
        if (labels.Length != 2 || labels.Distinct().Count() != 2)
            throw new InvalidOperationException("Фильтр не различает одинаково названные источники.");
        sources.SelectedValue = first.Id;
        Control<ComboBox>("ReportOutcomeFilter").SelectedIndex = 2;
        if (grid.Items.Count != 1 || ((ExternalBackupReport)grid.Items[0]).MonitorId != first.Id)
            throw new InvalidOperationException("Фильтр ошибки или идентификатора источника не работает.");
        grid.SelectedIndex = 0;
        var details = Control<TextBox>("ReportDetails").Text;
        if (!details.Contains(failure.Error!) || !details.Contains(first.Id.ToString()) || !details.Contains("не подтверждает наличие"))
            throw new InvalidOperationException("Подробности SQL потеряли источник, причину ошибки или пояснение результата.");
        Control<ComboBox>("ReportOutcomeFilter").SelectedIndex = 1;
        Control<TextBox>("ReportSearch").Text = "FIXTURE.DUMP";
        if (grid.Items.Count != 1) throw new InvalidOperationException("Совместный поиск пути и фильтры не работают.");
        grid.SelectedIndex = 0;
        var selectedId = ((ExternalBackupReport)grid.SelectedItem).ReportId;
        Click("RefreshReportsButton");
        await WaitUntil(() => window.SmokeReady, "Выбор наблюдения не обновился.");
        if ((grid.SelectedItem as ExternalBackupReport)?.ReportId != selectedId || sources.SelectedValue is not Guid id || id != first.Id
            || Control<TextBox>("ReportSearch").Text != "FIXTURE.DUMP" || Control<ComboBox>("ReportOutcomeFilter").SelectedIndex != 1)
            throw new InvalidOperationException("Обновление истории сбросило выбор или фильтры.");
        Control<TextBox>("ReportSearch").Text = "нет-соответствий";
        if (grid.Items.Count != 0 || grid.SelectedItem is not null || !Control<TextBox>("ReportDetails").Text.StartsWith("Выберите"))
            throw new InvalidOperationException("Фильтр оставил подробности скрытого наблюдения.");
        Click("ResetReportFiltersButton");
        if (grid.Items.Count != 3) throw new InvalidOperationException("Сброс фильтров не восстановил наблюдения.");
        grid.SelectedItem = grid.Items.OfType<ExternalBackupReport>().Single(report => report.Observation.Outcome == ExternalBackupOutcome.Failed);
        window.Width = 780; window.Height = 580;
        Control<Expander>("ReportDetailsPanel").IsExpanded = true;
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.ContextIdle);
        var scroll = FindDescendant<ScrollViewer>(grid);
        if (scroll is null || scroll.ExtentWidth > scroll.ViewportWidth + 1)
            throw new InvalidOperationException($"История мониторинга требует горизонтальной прокрутки: {scroll?.ExtentWidth}/{scroll?.ViewportWidth}.");
        var visible = grid.ItemContainerGenerator.ContainerFromItem(grid.Items[0]) as DataGridRow;
        if (visible is null || visible.ActualHeight < 25 || !Control<Button>("CollectMonitorButton").IsVisible
            || Control<Expander>("SourceSettings").IsExpanded)
            throw new InvalidOperationException("Компактный мониторинг скрывает строку истории или ручную проверку.");
        screenshot("sql-monitoring-filtered-compact", window);
        if (JsonSerializer.Serialize(await outbox.ReadAsync()) != before || JsonSerializer.Serialize(await monitorStore.LoadAsync()) != beforeMonitors)
            throw new InvalidOperationException("Фильтрация изменила историю наблюдений или настройки источников.");
    }
}
