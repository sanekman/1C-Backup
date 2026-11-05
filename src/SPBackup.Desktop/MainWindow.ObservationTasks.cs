using System.IO;
using System.Windows;
using System.Windows.Data;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private IReadOnlyList<ExternalBackupTask> observationTasks = [];
    private bool refreshingObservations;
    private ExternalBackupMonitorStore ObservationStore => new(Path.Combine(DesktopPaths.DataDirectory, "external-monitors.json"));

    private static bool MatchesTask(string name, string source, string query) => query.Length == 0
        || name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || source.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private void BindAgentTasks()
    {
        var items = jobs.Cast<object>().Concat(observationTasks).ToArray();
        if (Jobs.ItemsSource is object[] current && current.SequenceEqual(items)) return;
        var selected = Jobs.SelectedItem switch { BackupJob job => job.Id, ExternalBackupTask task => task.Id, _ => Guid.Empty };
        var sorting = Jobs.ItemsSource is null ? [] : CollectionViewSource.GetDefaultView(Jobs.ItemsSource).SortDescriptions.ToArray();
        Jobs.ItemsSource = items;
        var view = CollectionViewSource.GetDefaultView(items);
        foreach (var sort in sorting) view.SortDescriptions.Add(sort);
        ApplyJobFilter();
        Jobs.SelectedItem = items.FirstOrDefault(item => item switch
        { BackupJob job => job.Id == selected, ExternalBackupTask task => task.Id == selected, _ => false });
    }

    internal Task SmokeRefreshObservationTasksAsync() => RefreshObservationTasksAsync();

    private async Task RefreshObservationTasksAsync()
    {
        if (refreshingObservations || agentSettings is null) return;
        refreshingObservations = true;
        try
        {
            var monitors = await ObservationStore.LoadAsync();
            var reports = await new ExternalBackupOutbox(Path.Combine(DesktopPaths.DataDirectory, "external-backups.json")).ReadAsync();
            var polls = await new ExternalBackupPollingStatusStore(DesktopPaths.DataDirectory).ReadAsync();
            observationTasks = ExternalBackupTask.Create(monitors, reports, polls, agentSettings.AgentId);
            BindAgentTasks();
        }
        catch (Exception error)
        {
            DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, error);
            Status.Text = "Не удалось обновить задания наблюдения: " + error.Message + ". Сохранённые настройки не изменены.";
        }
        finally { refreshingObservations = false; }
    }

    private void NewTaskOptions_Click(object sender, RoutedEventArgs e)
    {
        if (!NewTaskOptionsButton.IsEnabled) return;
        NewTaskOptionsButton.ContextMenu.PlacementTarget = NewTaskOptionsButton;
        NewTaskOptionsButton.ContextMenu.IsOpen = true;
    }

    private async void NewObservationTask_Click(object sender, RoutedEventArgs e)
    {
        if (NewObservationTaskButton.IsEnabled) await OpenObservationTaskAsync(null, createNew: true);
    }

    private async Task OpenObservationTaskAsync(Guid? id, bool createNew = false, bool collect = false)
    {
        if (agentSettings is null) return;
        new ExternalMonitoringWindow(agentSettings, id, createNew, collect) { Owner = this }.ShowDialog();
        await RefreshObservationTasksAsync();
    }

    private async Task ToggleObservationTaskAsync(ExternalBackupTask task)
    {
        try
        {
            var monitors = await ObservationStore.LoadAsync();
            await ObservationStore.SaveAsync(monitors.Select(monitor => monitor.Id == task.Id
                ? monitor with { Enabled = !monitor.Enabled } : monitor).ToList());
            await RefreshObservationTasksAsync();
            Status.Text = task.Enabled ? "Опрос задания выключен. История сохранена." : "Опрос задания включён. Для выполнения должен работать агент или служба.";
        }
        catch (Exception error) { Status.Text = "Не удалось изменить опрос: " + error.Message; }
    }

    private async Task DeleteObservationTaskAsync(ExternalBackupTask task)
    {
        if (MessageBox.Show(this, "Удалить задание наблюдения «" + task.Name + "»? История и внешние копии сохранятся.",
            "SP Backup", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            var monitors = await ObservationStore.LoadAsync();
            await ObservationStore.SaveAsync(monitors.Where(monitor => monitor.Id != task.Id).ToList());
            await RefreshObservationTasksAsync();
            Status.Text = "Задание наблюдения удалено. История сохранена; уже начатый опрос может завершиться.";
        }
        catch (Exception error) { Status.Text = "Не удалось удалить задание: " + error.Message; }
    }
}
