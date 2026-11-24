using System.IO;
using System.Windows;
using System.Windows.Controls;
using SPBackup.Core;

namespace SPBackup.Desktop;

internal static partial class DesktopSmoke
{
    private static async Task VerifySqlMonitorCredentialsAsync(ExternalMonitoringWindow window, Action<string, Window> screenshot)
    {
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Click(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var path = Path.Combine(DesktopPaths.DataDirectory, "external-monitors.json");
        var monitors = new ExternalBackupMonitorStore(path); var credentials = new WindowsSqlServerCredentialStore();
        const string secret = "fixture-only-history-secret"; string? key = null;
        try
        {
            Click("NewMonitorButton"); Control<ComboBox>("SourceKind").SelectedValue = ExternalBackupSourceKind.SqlServerHistory;
            Control<TextBox>("MonitorName").Text = "SQL — мониторинг по логину";
            Control<TextBox>("SqlServer").Text = "sql-monitor.example";
            if (Control<ComboBox>("SqlAuthentication").SelectedValue is not SqlServerAuthentication.Windows)
                throw new InvalidOperationException("Новый источник унаследовал чужой SQL-логин.");
            Control<ComboBox>("SqlAuthentication").SelectedValue = SqlServerAuthentication.SqlServer;
            Click("SaveMonitorButton"); await WaitUntil(() => window.SmokeReady, "Проверка источника не завершилась.");
            if (!Control<TextBlock>("Status").Text.Contains("логин и пароль"))
                throw new InvalidOperationException("Источник SQL сохранён без пароля.");
            window.SqlCredentialsDialogOpened = dialog => dialog.Dispatcher.BeginInvoke(() =>
            {
                key = dialog.CredentialKey;
                ((TextBox)dialog.FindName("Username")).Text = "history-reader";
                ((PasswordBox)dialog.FindName("Password")).Password = secret;
                ((Button)dialog.FindName("CredentialSaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            Click("SqlCredentialsButton");
            if (key is null || (await credentials.GetAsync(key)).Password != secret)
                throw new InvalidOperationException("Пароль мониторинга не сохранён в Windows.");
            if (Control<TextBlock>("Status").Text.Contains("Не удалось сохранить")
                || Control<Expander>("ReportDetailsPanel").IsExpanded || Control<Expander>("PollStatusesPanel").IsExpanded)
                throw new InvalidOperationException("Новый источник сохраняет старую ошибку или подробности прежнего наблюдения.");
            Control<StackPanel>("SqlCredentialsPanel").BringIntoView(); window.UpdateLayout();
            screenshot("sql-monitor-login", window);
            Click("SaveMonitorButton"); await WaitUntil(() => window.SmokeReady, "Источник SQL не сохранён.");
            var saved = (await monitors.LoadAsync()).Single(m => m.Name == "SQL — мониторинг по логину");
            if (saved.SqlCredentialKey != key || saved.SqlAuthentication != SqlServerAuthentication.SqlServer)
                throw new InvalidOperationException("Источник потерял SQL-привязку.");
            var json = await File.ReadAllTextAsync(path);
            if (json.Contains(secret) || json.Contains("history-reader")) throw new InvalidOperationException("Данные входа попали в JSON.");
            Click("NewMonitorButton");
            Control<ComboBox>("Sources").SelectedValue = saved;
            if (!Control<TextBlock>("SqlCredentialStatus").Text.Contains("сохранены"))
                throw new InvalidOperationException("Редактор не восстановил SQL-привязку источника.");
            Control<TextBox>("SqlServer").Text = "other-sql.example";
            Click("SaveMonitorButton"); await WaitUntil(() => window.SmokeReady, "Смена сервера не проверена.");
            if (!Control<TextBlock>("Status").Text.Contains("логин и пароль") || (await monitors.LoadAsync()).Single(m => m.Id == saved.Id) != saved)
                throw new InvalidOperationException("Источник передал прежний пароль другому серверу.");
            Control<ComboBox>("SqlAuthentication").SelectedValue = SqlServerAuthentication.Windows;
            Click("SaveMonitorButton"); await WaitUntil(() => window.SmokeReady, "Windows-вход источника не сохранён.");
            var updated = (await monitors.LoadAsync()).Single(m => m.Id == saved.Id);
            if (updated.SqlCredentialKey is not null || updated.SqlAuthentication != SqlServerAuthentication.Windows)
                throw new InvalidOperationException("Windows-вход сохранил ненужную ссылку на SQL-пароль.");
            if ((await credentials.GetAsync(key)).Password != secret) throw new InvalidOperationException("Смена входа удалила прежний пароль.");
            Click("NewMonitorButton");
            if (Control<ComboBox>("SqlAuthentication").SelectedValue is not SqlServerAuthentication.Windows
                || Control<TextBlock>("SqlCredentialStatus").Text.Contains("Данные входа сохранены"))
                throw new InvalidOperationException("Новый источник унаследовал привязку пароля.");
        }
        finally { window.SqlCredentialsDialogOpened = null; if (key is not null) credentials.Delete(key); }
    }
}
