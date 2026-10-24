using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace OneCBackup.Desktop;

public partial class MainWindow
{
    private sealed record CopyJobChoice(Guid? Id, string Label);
    private List<BackupCopyRow> copyRows = [];
    private bool updatingCopyFilters;
    private string? copyCheckReadError;

    private void CopiesView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (CopyDetailsScroll is not null) CopyDetailsScroll.MaxHeight = ActualHeight < 650 ? 60 : 150;
    }

    private void UpdateCopyJobChoices()
    {
        var selectedId = (CopyJobFilter.SelectedItem as CopyJobChoice)?.Id;
        var names = jobs.Select(job => new CopyJobChoice(job.Id, job.Name))
            .Concat(copyRows.Select(row => new CopyJobChoice(row.Record.JobId, row.JobName)))
            .DistinctBy(choice => choice.Id).ToList();
        var duplicates = names.GroupBy(choice => choice.Label, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var choices = new[] { new CopyJobChoice(null, "Все задания") }.Concat(names
            .OrderBy(choice => choice.Label, StringComparer.CurrentCultureIgnoreCase)
            .Select(choice => duplicates.Contains(choice.Label)
                ? choice with { Label = choice.Label + " · " + choice.Id!.Value.ToString("N")[..8] } : choice)).ToList();
        updatingCopyFilters = true;
        try
        {
            CopyJobFilter.ItemsSource = choices;
            CopyJobFilter.SelectedItem = choices.FirstOrDefault(choice => choice.Id == selectedId) ?? choices[0];
        }
        finally { updatingCopyFilters = false; }
    }

    private void CopyFilter_Changed(object sender, RoutedEventArgs e) => ApplyCopyFilter();

    private void ApplyCopyFilter()
    {
        if (updatingCopyFilters || Copies is null || CopySearch is null || CopyJobFilter is null
            || CopyStorageFilter is null || CopiesSummary is null || Copies.ItemsSource is null) return;
        var jobId = (CopyJobFilter.SelectedItem as CopyJobChoice)?.Id;
        var storage = (CopyStorageFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        var query = CopySearch.Text.Trim();
        bool Matches(BackupCopyRow row) => (jobId is null || row.Record.JobId == jobId)
            && (string.IsNullOrEmpty(storage) || row.StorageCategory == storage)
            && (query.Length == 0 || row.JobName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.DisplayLocation.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Location.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (Copies.SelectedItem is BackupCopyRow selected && !Matches(selected)) Copies.SelectedItem = null;
        var view = CollectionViewSource.GetDefaultView(Copies.ItemsSource);
        view.Filter = item => item is BackupCopyRow row && Matches(row);
        var count = Copies.Items.Count;
        CopiesSummary.Text = copyRows.Count == 0 ? "Копий пока нет. Создайте первую копию в разделе «Задания»."
            : count == 0 ? $"Копий по выбранным условиям нет · Всего: {copyRows.Count}"
            : $"Показано копий: {count} из {copyRows.Count}";
        if (copyCheckReadError is not null) CopiesSummary.Text += " · Результаты проверок недоступны";
        CopiesSummary.ToolTip = copyCheckReadError;
        UpdateRunControls();
    }

    private void ResetCopyFilters_Click(object sender, RoutedEventArgs e)
    {
        CopyJobFilter.SelectedIndex = 0;
        CopyStorageFilter.SelectedIndex = 0;
        CopySearch.Clear();
    }

    private void SelectCopiesForJob(Guid jobId)
    {
        CopyStorageFilter.SelectedIndex = 0;
        CopySearch.Clear();
        CopyJobFilter.SelectedItem = CopyJobFilter.Items.OfType<CopyJobChoice>().FirstOrDefault(choice => choice.Id == jobId);
        ApplyCopyFilter();
    }
}
