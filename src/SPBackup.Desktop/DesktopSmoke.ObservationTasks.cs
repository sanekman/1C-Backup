using System.IO;
using System.Windows;
using System.Windows.Controls;
using SPBackup.Core;

namespace SPBackup.Desktop;

internal static partial class DesktopSmoke
{
    private static async Task VerifyObservationTasksAsync(MainWindow window, Action<string, Window> screenshot)
    {
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var search = Control<TextBox>("JobSearch");
        var grid = Control<DataGrid>("Jobs");
        Control<Button>("JobsNavigation").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        search.Clear();
        await window.SmokeRefreshObservationTasksAsync();
        var tasks = grid.Items.OfType<ExternalBackupTask>().ToArray();
        var stored = await new ExternalBackupMonitorStore(Path.Combine(DesktopPaths.DataDirectory, "external-monitors.json")).LoadAsync();
        if (tasks.Length != stored.Count || tasks.Length == 0)
            throw new InvalidOperationException("Задания наблюдения отсутствуют в общем списке.");
        if (!tasks.Select(task => task.Id).Order().SequenceEqual(stored.Select(monitor => monitor.Id).Order()))
            throw new InvalidOperationException("Переход в общий список изменил идентификаторы наблюдений.");
        var selected = tasks.First(task => task.Latest is not null);
        grid.SelectedItem = selected;
        window.UpdateLayout();
        if (!Control<Button>("EditJobButton").IsEnabled || !Control<Button>("ShowLatestRunButton").IsEnabled
            || !Control<Button>("DeleteButton").IsEnabled || !Control<Button>("JobScheduleButton").IsEnabled
            || !Equals(Control<Button>("RunButton").Content, "Проверить сейчас")
            || Control<Button>("CheckStorageButton").IsEnabled || Control<Button>("DuplicateJobButton").IsEnabled)
            throw new InvalidOperationException("Действия задания наблюдения смешались с копированием.");
        var column = grid.Columns.Single(column => Equals(column.Header, "Последний запуск"));
        grid.ScrollIntoView(selected, column); window.UpdateLayout();
        var element = column.GetCellContent(selected);
        var badge = element is null ? null : FindDescendant<ContentControl>(element)?.Content as JobRunBadge;
        if (badge?.Label != selected.ResultDescription)
            throw new InvalidOperationException("Результат наблюдения не привязался к общей таблице.");
        var id = selected.Id;
        await window.SmokeRefreshObservationTasksAsync();
        if (grid.SelectedItem is not ExternalBackupTask refreshed || refreshed.Id != id)
            throw new InvalidOperationException("Обновление наблюдений сбросило выбор.");
        search.Text = selected.Monitor.Kind == ExternalBackupSourceKind.SqlServerHistory ? "Наблюдение MS SQL" : "Наблюдение PostgreSQL";
        if (!grid.Items.OfType<ExternalBackupTask>().Any() || grid.Items.OfType<BackupJob>().Any())
            throw new InvalidOperationException("Поиск заданий не находит наблюдения по типу.");
        screenshot("jobs-sql-observation", window);
        search.Clear();
        grid.SelectedItem = grid.Items.OfType<BackupJob>().FirstOrDefault();
        if (!Equals(Control<Button>("RunButton").Content, "Создать копию"))
            throw new InvalidOperationException("После наблюдения действие резервного копирования не восстановилось.");
        await VerifyNewObservationTaskAsync(window);
    }

    private static async Task VerifyNewObservationTaskAsync(MainWindow owner)
    {
        var choices = (Button)owner.FindName("NewTaskOptionsButton");
        choices.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!choices.ContextMenu.IsOpen || !choices.ContextMenu.Items.OfType<MenuItem>()
            .Any(item => Equals(item.Header, "Наблюдение SQL") && item.IsEnabled))
            throw new InvalidOperationException("В выборе типа нового задания нет наблюдения SQL.");
        choices.ContextMenu.IsOpen = false;
        var store = new ExternalBackupMonitorStore(Path.Combine(DesktopPaths.DataDirectory, "external-monitors.json"));
        var original = await store.LoadAsync();
        var settings = await new AgentSettingsStore(Path.Combine(DesktopPaths.DataDirectory, "agent.json")).LoadAsync();
        var editor = new ExternalMonitoringWindow(settings, createNew: true) { Owner = owner };
        editor.Show();
        try
        {
            await WaitUntil(() => editor.SmokeReady, "Новое задание наблюдения не открылось.");
            T Control<T>(string name) where T : FrameworkElement => (T)editor.FindName(name);
            Control<TextBox>("MonitorName").Text = "Наблюдение выбранной базы";
            Control<TextBox>("SqlServer").Text = "fixture-sql.example";
            Control<CheckBox>("MonitorEnabled").IsChecked = false;
            Control<Button>("SaveMonitorButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => editor.SmokeReady, "Проверка имени базы не завершилась.");
            if (!Control<TextBlock>("Status").Text.Contains("конкретную базу"))
                throw new InvalidOperationException("Новое наблюдение разрешает незаданный выбор базы.");
            Control<TextBox>("Database").Text = "Accounting";
            Control<Button>("SaveMonitorButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => editor.SmokeReady, "Задание наблюдения не сохранилось.");
            var saved = (await store.LoadAsync()).Single(task => task.Name == "Наблюдение выбранной базы");
            if (saved.DatabaseName != "Accounting" || saved.Enabled)
                throw new InvalidOperationException("Новое наблюдение потеряло выбранную базу или включилось само.");
            editor.Close();
            await owner.SmokeRefreshObservationTasksAsync();
            var grid = (DataGrid)owner.FindName("Jobs");
            if (!grid.Items.OfType<ExternalBackupTask>().Any(task => task.Id == saved.Id))
                throw new InvalidOperationException("Сохранённое наблюдение не появилось среди заданий агента.");
        }
        finally
        {
            editor.Close();
            await store.SaveAsync(original);
            await owner.SmokeRefreshObservationTasksAsync();
        }
    }
}
