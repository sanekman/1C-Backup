using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

internal static partial class DesktopSmoke
{
    private static async Task VerifySqlCredentialsAsync(MainWindow window, Action<string> screenshot)
    {
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Click(string name)
        {
            window.UpdateLayout(); var button = Control<Button>(name);
            if (!button.IsVisible || !button.IsEnabled) throw new InvalidOperationException("Действие SQL недоступно: " + name);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
        }
        var store = new WindowsSqlServerCredentialStore();
        var jobStore = new JobStore(Path.Combine(DesktopPaths.DataDirectory, "jobs.json"));
        var root = Path.GetDirectoryName(DesktopPaths.DataDirectory)!;
        var export = Path.Combine(root, "sql-login-transfer.json");
        const string secret = "fixture-only-sql-password";
        string? key = null;
        try
        {
            Click("JobsNavigation"); Click("NewJobButton");
            Control<ComboBox>("Kind").SelectedItem = DatabaseKind.SqlServer;
            Control<TextBox>("JobName").Text = "SQL — сохранённый логин";
            Control<TextBox>("Source").Text = "fixture_db";
            Control<TextBox>("SqlBackupServer").Text = "sql.example";
            Control<TextBox>("SqlServerDirectory").Text = @"C:\SQLBackups";
            Control<TextBox>("SqlAgentDirectory").Text = Path.Combine(root, "sql-stage");
            if (Control<ComboBox>("SqlAuthenticationPicker").SelectedValue is not SqlServerAuthentication.Windows
                || Control<StackPanel>("SqlCredentialsPanel").IsVisible)
                throw new InvalidOperationException("Новое SQL-задание изменило прежний вход через Windows.");
            Control<ComboBox>("SqlAuthenticationPicker").SelectedValue = SqlServerAuthentication.SqlServer;
            Click("NextStepButton");
            if (!Control<TextBlock>("WizardError").IsVisible || !Control<TextBlock>("WizardError").Text.Contains("логин и пароль"))
                throw new InvalidOperationException("Мастер пропустил SQL-логин без сохранённых учётных данных.");
            window.SqlCredentialsDialogOpened = dialog => dialog.Dispatcher.BeginInvoke(() =>
            {
                key = ((TextBlock)dialog.FindName("BindingName")).Text.Split(':', 2)[1].Trim();
                ((TextBox)dialog.FindName("Username")).Text = "fixture-backup-user";
                ((PasswordBox)dialog.FindName("Password")).Password = secret;
                ((Button)dialog.FindName("CredentialSaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            Click("SqlCredentialsButton");
            if (key is null || (await store.GetAsync(key)).Password != secret)
                throw new InvalidOperationException("Мастер не сохранил пароль SQL в Windows.");
            if (Control<TextBlock>("WizardError").Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("После сохранения SQL-пароля осталось устаревшее сообщение об ошибке.");
            Control<StackPanel>("SqlCredentialsPanel").BringIntoView();
            screenshot("sql-login-source");
            Click("NextStepButton");
            Control<TextBox>("Destination").Text = Path.Combine(root, "sql-copies");
            Click("NextStepButton");
            Control<ComboBox>("Schedule").SelectedValue = ScheduleKind.Manual;
            Click("NextStepButton"); Click("SaveButton");
            await WaitUntil(() => window.SmokeReady && Control<Border>("JobListCard").IsVisible, "SQL-логин не сохранён в задании.");
            var job = (await jobStore.LoadAsync()).Single(job => job.Name == "SQL — сохранённый логин");
            if (job.SqlServer?.Authentication != SqlServerAuthentication.SqlServer || job.SqlServer.CredentialKey != key)
                throw new InvalidOperationException("SQL-задание потеряло способ входа или привязку пароля.");
            if ((await File.ReadAllTextAsync(Path.Combine(DesktopPaths.DataDirectory, "jobs.json"))).Contains(secret))
                throw new InvalidOperationException("Пароль SQL попал в файл заданий.");
            Control<DataGrid>("Jobs").SelectedItem = Control<DataGrid>("Jobs").Items.OfType<BackupJob>().Single(item => item.Id == job.Id);
            Click("EditJobButton");
            if (Control<ComboBox>("SqlAuthenticationPicker").SelectedValue is not SqlServerAuthentication.SqlServer
                || !Control<TextBlock>("SqlCredentialStatus").Text.Contains("сохранены"))
                throw new InvalidOperationException("Редактор не восстановил сохранённый способ входа SQL.");
            Click("CancelEditorButton");
            await BackupJobTransfer.ExportAsync(job, export);
            store.Delete(key);
            window.JobImportDialogOverride = dialog => { dialog.FileName = export; return true; };
            Control<MenuItem>("ImportJobSettingsMenuItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitUntil(() => window.SmokeReady && Control<Border>("EditorCard").IsVisible, "SQL-импорт не открыл мастер.");
            Click("NextStepButton");
            if (!Control<TextBlock>("WizardError").IsVisible || !Control<TextBlock>("WizardError").Text.Contains("логин и пароль"))
                throw new InvalidOperationException("Импорт SQL принял отсутствующий пароль Windows.");
            screenshot("sql-login-import-missing-credentials");
            Click("CancelEditorButton");
            Click("NewJobButton"); Control<ComboBox>("Kind").SelectedItem = DatabaseKind.SqlServer;
            if (Control<ComboBox>("SqlAuthenticationPicker").SelectedValue is not SqlServerAuthentication.Windows)
                throw new InvalidOperationException("Новое задание унаследовало SQL-логин предыдущего.");
            Click("CancelEditorButton");
        }
        finally
        {
            window.SqlCredentialsDialogOpened = null; window.JobImportDialogOverride = null;
            if (key is not null) store.Delete(key);
            File.Delete(export);
        }
    }
}
