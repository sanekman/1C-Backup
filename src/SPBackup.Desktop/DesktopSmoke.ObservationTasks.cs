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
            || !Control<MenuItem>("DeleteButton").IsEnabled || !Control<Button>("JobScheduleButton").IsEnabled
            || !Equals(Control<Button>("RunButton").Content, "Проверить сейчас")
            || Control<Button>("CheckStorageButton").IsEnabled || Control<MenuItem>("DuplicateJobButton").IsEnabled)
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
            Control<CheckBox>("FreshnessEnabled").IsChecked = true;
            Control<TextBox>("FreshnessHours").Text = "0";
            Control<Button>("SaveMonitorButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => editor.SmokeReady, "Проверка срока копии не завершилась.");
            if (!Control<TextBlock>("Status").Text.Contains("часов") || (await store.LoadAsync()).Count != original.Count)
                throw new InvalidOperationException("Редактор сохранил неверный срок ожидания копии.");
            Control<TextBox>("FreshnessHours").Text = "26";
            editor.Width = 900;
            editor.Height = 580;
            Control<Expander>("SqlCertificateSettings").IsExpanded = true;
            Control<CheckBox>("SqlTrustServerCertificate").BringIntoView();
            editor.UpdateLayout();
            var save = Control<Button>("SaveMonitorButton");
            if (!save.IsVisible || save.ActualHeight < 20
                || save.TranslatePoint(new Point(0, save.ActualHeight), editor).Y >= editor.ActualHeight)
                throw new InvalidOperationException("Компактное окно скрывает сохранение при настройке сертификата.");
            await VerifySqlCertificateWarningAsync(editor);
            Control<Button>("SaveMonitorButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => editor.SmokeReady, "Задание наблюдения не сохранилось.");
            var saved = (await store.LoadAsync()).Single(task => task.Name == "Наблюдение выбранной базы");
            if (saved.DatabaseName != "Accounting" || saved.Enabled || saved.MaximumBackupAgeHours != 26 || !saved.TrustServerCertificate)
                throw new InvalidOperationException("Новое наблюдение потеряло выбранную базу или включилось само.");
            Control<ComboBox>("Sources").SelectedValue = saved;
            if (Control<CheckBox>("FreshnessEnabled").IsChecked != true || Control<TextBox>("FreshnessHours").Text != "26" || Control<CheckBox>("SqlTrustServerCertificate").IsChecked != true)
                throw new InvalidOperationException("Редактор не восстановил сохранённый срок копии.");
            Control<CheckBox>("SqlAllowUnencrypted").IsChecked = true;
            Control<Button>("CollectMonitorButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!Control<TextBlock>("Status").Text.Contains("Параметры сертификата или шифрования изменены"))
                throw new InvalidOperationException("Проверка SQL началась с прежними параметрами подключения.");
            Control<Button>("SaveMonitorButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => editor.SmokeReady, "Параметры шифрования не сохранились.");
            if (!(await store.LoadAsync()).Single(task => task.Id == saved.Id).AllowUnencryptedConnection)
                throw new InvalidOperationException("Настройка необязательного шифрования потерялась при сохранении.");
            Control<Button>("NewMonitorButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (Control<CheckBox>("FreshnessEnabled").IsChecked == true || Control<CheckBox>("SqlTrustServerCertificate").IsChecked == true
                || Control<CheckBox>("SqlAllowUnencrypted").IsChecked == true)
                throw new InvalidOperationException("Новое наблюдение унаследовало настройки прежнего задания.");
            editor.Close();
            await VerifyExistingAllDatabaseObservationAsync(owner, settings, store, original, saved);
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

    private static async Task VerifyExistingAllDatabaseObservationAsync(MainWindow owner, AgentSettings settings,
        ExternalBackupMonitorStore store, IReadOnlyList<ExternalBackupMonitor> original, ExternalBackupMonitor monitor)
    {
        // Legacy jobs may cover all databases and have an existing service credential.
        // Editing TLS must not fetch the already saved credential from the desktop Vault.
        monitor = monitor with
        {
            DatabaseName = "", MaximumBackupAgeHours = null, SqlAuthentication = SqlServerAuthentication.SqlServer, SqlCredentialKey = Guid.NewGuid().ToString("N"),
            TrustServerCertificate = false, AllowUnencryptedConnection = false
        };
        await store.SaveAsync(original.Append(monitor).ToArray());
        var editor = new ExternalMonitoringWindow(settings, monitor.Id) { Owner = owner, Width = 900, Height = 580 };
        editor.Show();
        try
        {
            await WaitUntil(() => editor.SmokeReady, "Сохранённое наблюдение всех баз не открылось.");
            var protection = (Expander)editor.FindName("SqlCertificateSettings");
            protection.IsExpanded = true;
            ((CheckBox)editor.FindName("SqlTrustServerCertificate")).BringIntoView();
            await VerifySqlCertificateWarningAsync(editor);
            await VerifySqlProtectionWarningAsync(editor, "SqlAllowUnencrypted", "MS SQL · шифрование по требованию сервера");
            ((Button)editor.FindName("SaveMonitorButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => editor.SmokeReady, "Параметры сертификата прежнего задания не сохранились.");
            var saved = (await store.LoadAsync()).Single(value => value.Id == monitor.Id);
            if (saved != (monitor with { TrustServerCertificate = true, AllowUnencryptedConnection = true }))
                throw new InvalidOperationException("Редактор не сохранил параметры сертификата прежнего наблюдения всех баз.");
        }
        finally { editor.Close(); }

        var reopened = new ExternalMonitoringWindow(settings, monitor.Id) { Owner = owner };
        reopened.Show();
        try
        {
            await WaitUntil(() => reopened.SmokeReady, "Прежнее задание не открылось после сохранения.");
            var trust = (CheckBox)reopened.FindName("SqlTrustServerCertificate");
            var allowUnencrypted = (CheckBox)reopened.FindName("SqlAllowUnencrypted");
            if (trust.IsChecked != true || allowUnencrypted.IsChecked != true)
                throw new InvalidOperationException("Прежнее задание не восстановило оба сохранённых параметра подключения.");
            trust.IsChecked = allowUnencrypted.IsChecked = false;
            ((Button)reopened.FindName("SaveMonitorButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => reopened.SmokeReady, "Исходные параметры подключения не сохранились.");
            if ((await store.LoadAsync()).Single(value => value.Id == monitor.Id) != monitor)
                throw new InvalidOperationException("Возврат исходных параметров изменил другие настройки наблюдения.");
        }
        finally { reopened.Close(); }
    }
}
