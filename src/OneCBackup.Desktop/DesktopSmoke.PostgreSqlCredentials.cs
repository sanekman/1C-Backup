using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

internal static partial class DesktopSmoke
{
    private static async Task VerifyPostgreSqlCredentialsAsync(MainWindow window, Action<string> screenshot)
    {
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Click(string name)
        {
            var button = Control<Button>(name); button.BringIntoView(); window.UpdateLayout();
            if (!button.IsEnabled || !button.IsVisible) throw new InvalidOperationException("Действие PostgreSQL недоступно: " + name);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
        }
        void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        var root = Path.Combine(Path.GetDirectoryName(DesktopPaths.DataDirectory)!, "pg-password-ui"); Directory.CreateDirectory(root);
        var export = Path.Combine(root, "settings.json");
        foreach (var name in new[] { "pg_dump.exe", "pg_restore.exe" }) File.WriteAllText(Path.Combine(root, name), "fixture, never execute");
        const string secret = "fixture-pg-saved-password:with\\escapes";
        var store = new WindowsPostgreSqlCredentialStore(); string? key = null;
        async Task Import(string path)
        {
            window.JobImportDialogOverride = dialog => { dialog.FileName = path; return true; };
            ((MenuItem)window.FindName("ImportJobSettingsMenuItem")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitUntil(() => window.SmokeReady && Control<Border>("EditorCard").IsVisible, "Импорт PostgreSQL не открыл мастер.");
        }
        void RequireNewPassword()
        {
            Require(Control<ComboBox>("PgAuthenticationPicker").SelectedIndex == 0, "Импорт молча изменил способ входа PostgreSQL.");
            Click("NextStepButton");
            Require(Control<TextBlock>("WizardError").IsVisible && Control<TextBlock>("WizardError").Text.Contains("пароль"),
                "Мастер разрешил сохранённый пароль без доступной доверенной привязки.");
        }
        try
        {
            Click("JobsNavigation"); Click("NewJobButton");
            Control<ComboBox>("Kind").SelectedItem = DatabaseKind.PostgreSql;
            Require(Control<ComboBox>("PgAuthenticationPicker").SelectedIndex == 0, "Новое задание не предлагает удобное сохранение пароля.");
            Control<TextBox>("JobName").Text = "PostgreSQL — сохранённый пароль";
            Control<TextBox>("Source").Text = "fixture_db"; Control<TextBox>("PgHost").Text = "pg.example";
            Control<TextBox>("PgPort").Text = "5433"; Control<TextBox>("PgUsername").Text = "fixture-user";
            Control<TextBox>("PgDumpPath").Text = Path.Combine(root, "pg_dump.exe");
            Control<TextBox>("PgRestorePath").Text = Path.Combine(root, "pg_restore.exe");
            RequireNewPassword();
            window.PostgreSqlCredentialsDialogOpened = dialog =>
            {
                key = dialog.CredentialKey;
                dialog.Dispatcher.BeginInvoke(() =>
                {
                    var username = (TextBox)dialog.FindName("Username");
                    Require(username.Text == "fixture-user" && username.IsReadOnly, "Окно пароля изменило выбранного пользователя PostgreSQL.");
                    ((PasswordBox)dialog.FindName("Password")).Password = secret;
                    ((Button)dialog.FindName("CredentialSaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
            };
            Click("PgCredentialsButton");
            Require(!Control<TextBlock>("WizardError").IsVisible, "После ввода пароля осталась старая ошибка.");
            Control<StackPanel>("PgCredentialsPanel").BringIntoView(); screenshot("postgres-password-source");
            Click("NextStepButton"); Control<TextBox>("Destination").Text = Path.Combine(root, "copies");
            Click("NextStepButton"); Control<ComboBox>("Schedule").SelectedValue = ScheduleKind.Manual;
            Click("NextStepButton"); Click("SaveButton");
            await WaitUntil(() => window.SmokeReady && Control<Border>("JobListCard").IsVisible, "PostgreSQL не сохранил ссылку на пароль.");
            var jobStore = new JobStore(Path.Combine(DesktopPaths.DataDirectory, "jobs.json"));
            var job = (await jobStore.LoadAsync()).Single(job => job.Name == "PostgreSQL — сохранённый пароль");
            Require(key is not null && job.PostgreSql!.CredentialKey == key, "Привязка пароля PostgreSQL потеряна при сохранении.");
            Require((await store.GetAsync(key!)).Password == secret, "Пароль отсутствует в Windows Vault.");
            Require(!(await File.ReadAllTextAsync(Path.Combine(DesktopPaths.DataDirectory, "jobs.json"))).Contains("fixture-pg-saved-password"), "Пароль попал в JSON задания.");
            Click("EditJobButton");
            Require(Control<ComboBox>("PgAuthenticationPicker").SelectedIndex == 0 && Control<TextBlock>("PgCredentialStatus").Text.Contains("сохранён"),
                "Повторное открытие потеряло способ входа.");
            Control<TextBox>("PgHost").Text = "other.example";
            RequireNewPassword();
            Click("CancelEditorButton");
            await BackupJobTransfer.ExportAsync(job, export);
            var json = await File.ReadAllTextAsync(export);
            Require(!json.Contains("fixture-pg-saved-password"), "Экспорт раскрыл пароль PostgreSQL.");
            await Import(export); Click("NextStepButton");
            Require(Control<StackPanel>("StorageStep").IsVisible && window.HasUnsavedJob, "Известный импорт потерял доступную привязку или защиту черновика.");
            Click("CancelEditorButton");
            foreach (var field in new[] { "Host", "Port", "Username", "CredentialKey" })
            {
                var changed = JsonNode.Parse(json)!;
                var pg = changed["Job"]!["PostgreSql"]!;
                if (field == "Port") pg[field] = 5434;
                else pg[field] = field == "Host" ? "other-pg.example" : field == "Username" ? "other-user" : Guid.NewGuid().ToString("N");
                var path = Path.Combine(root, field + ".json"); await File.WriteAllTextAsync(path, changed.ToJsonString());
                await Import(path); RequireNewPassword();
                Require((await store.GetAsync(key!)).Password == secret, "Импорт изменил пароль исходного задания.");
                Click("CancelEditorButton");
            }
            store.Delete(key!);
            await Import(export); RequireNewPassword(); screenshot("postgres-import-password-required"); Click("CancelEditorButton");
            // An explicit legacy choice remains available and survives reopening.
            Click("EditJobButton"); Control<ComboBox>("PgAuthenticationPicker").SelectedIndex = 1;
            Click("NextStepButton"); Click("NextStepButton"); Click("NextStepButton"); Click("SaveButton");
            await WaitUntil(() => window.SmokeReady && Control<Border>("JobListCard").IsVisible, "Прежний способ входа PostgreSQL не сохранился.");
            Click("EditJobButton");
            Require(Control<ComboBox>("PgAuthenticationPicker").SelectedIndex == 1, "Прежнее задание потребовало новый пароль.");
            Click("CancelEditorButton");
        }
        finally
        {
            window.PostgreSqlCredentialsDialogOpened = null; window.JobImportDialogOverride = null;
            if (key is not null) store.Delete(key);
            Directory.Delete(root, true);
        }
    }
}
