using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class ExternalMonitoringWindow
{
    private sealed record MonitorSourceChoice(ExternalBackupMonitor Monitor, string Label);
    private ExternalBackupMonitor? SelectedMonitor => (Sources.SelectedItem as MonitorSourceChoice)?.Monitor;
    private sealed record ReportSourceChoice(Guid? Id, string Label);
    private List<ExternalBackupReport> reports = [];
    private bool updatingReportFilters;

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SourceEditorScroll is null || ReportDetails is null || PollStatuses is null) return;
        var compact = ActualHeight < 650;
        SourceEditorScroll.MaxHeight = compact ? 180 : 280;
        ReportDetails.Height = compact ? 60 : 100;
        PollStatuses.Height = compact ? 80 : 100;
        if (compact && PollStatusesPanel.IsExpanded && ReportDetailsPanel.IsExpanded) PollStatusesPanel.IsExpanded = false;
    }

    private void PollStatusesPanel_Expanded(object sender, RoutedEventArgs e)
    { if (ActualHeight < 650 && ReportDetailsPanel is not null) ReportDetailsPanel.IsExpanded = false; }

    private void ReportDetailsPanel_Expanded(object sender, RoutedEventArgs e)
    { if (ActualHeight < 650 && PollStatusesPanel is not null) PollStatusesPanel.IsExpanded = false; }

    private void UpdateMonitorControls()
    {
        var idle = ready && !busy && !lifetime.IsCancellationRequested;
        Sources.IsEnabled = NewMonitorButton.IsEnabled = idle;
        CollectMonitorButton.IsEnabled = DeleteMonitorButton.IsEnabled = idle && SelectedMonitor is not null;
    }

    private void BindMonitorSources(Guid? selectedId)
    {
        var duplicates = monitors.GroupBy(monitor => monitor.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var choices = monitors.Select(monitor => new MonitorSourceChoice(monitor,
            duplicates.Contains(monitor.Name) ? monitor.Id.ToString("N")[..8] + " · " + monitor.Name : monitor.Name)).ToList();
        Sources.ItemsSource = choices;
        Sources.SelectedItem = choices.FirstOrDefault(choice => choice.Monitor.Id == selectedId);
    }

    private string MonitorLabel(Guid id) => monitors.FirstOrDefault(monitor => monitor.Id == id)?.Name
        ?? "Удалённый источник · " + id.ToString("N")[..8];

    private void UpdateReportSourceChoices()
    {
        var selected = (ReportSourceFilter.SelectedItem as ReportSourceChoice)?.Id;
        var sources = monitors.Select(monitor => monitor.Id).Concat(reports.Select(report => report.MonitorId))
            .Distinct().Select(id => new ReportSourceChoice(id, MonitorLabel(id))).ToArray();
        var duplicateNames = sources.GroupBy(source => source.Label, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var choices = new[] { new ReportSourceChoice(null, "Все источники") }.Concat(sources.OrderBy(source => source.Label)
            .Select(source => duplicateNames.Contains(source.Label)
                ? source with { Label = source.Label + " · " + source.Id!.Value.ToString("N")[..8] } : source)).ToList();
        updatingReportFilters = true;
        try
        {
            ReportSourceFilter.ItemsSource = choices;
            ReportSourceFilter.SelectedItem = choices.FirstOrDefault(choice => choice.Id == selected) ?? choices[0];
        }
        finally { updatingReportFilters = false; }
    }

    private void ReportFilter_Changed(object sender, RoutedEventArgs e) => ApplyReportFilter();
    private void ApplyReportFilter()
    {
        if (updatingReportFilters || Reports is null || ReportsSummary is null || ReportSearch is null
            || ReportOutcomeFilter is null || ReportSourceFilter is null || Reports.ItemsSource is null) return;
        var monitor = (ReportSourceFilter.SelectedItem as ReportSourceChoice)?.Id;
        var outcome = (ReportOutcomeFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        var query = ReportSearch.Text.Trim();
        bool Matches(ExternalBackupReport report) => (monitor is null || report.MonitorId == monitor)
            && (string.IsNullOrEmpty(outcome) || report.Observation.Outcome == (outcome == "failed" ? ExternalBackupOutcome.Failed : ExternalBackupOutcome.Completed))
            && (query.Length == 0 || report.Observation.DatabaseName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || report.Observation.LocationDescription.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (report.Observation.Error?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        if (Reports.SelectedItem is ExternalBackupReport selected && !Matches(selected)) Reports.SelectedItem = null;
        CollectionViewSource.GetDefaultView(Reports.ItemsSource).Filter = item => item is ExternalBackupReport report && Matches(report);
        ReportsSummary.Text = reports.Count == 0 ? "Наблюдений пока нет. Сохраните источник и нажмите «Проверить источник»."
            : Reports.Items.Count == 0 ? $"По выбранным условиям наблюдений нет · Всего: {reports.Count}"
            : $"Показано наблюдений: {Reports.Items.Count} из {reports.Count}";
    }

    private void ResetReportFilters_Click(object sender, RoutedEventArgs e)
    { ReportSourceFilter.SelectedIndex = 0; ReportOutcomeFilter.SelectedIndex = 0; ReportSearch.Clear(); }
}
