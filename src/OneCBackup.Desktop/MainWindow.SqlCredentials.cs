using System.Windows;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

public partial class MainWindow
{
    private string? sqlCredentialKey;
    internal Action<FtpCredentialsWindow>? SqlCredentialsDialogOpened { get; set; }
    private SqlServerAuthentication SelectedSqlAuthentication => SqlAuthenticationPicker.SelectedValue is SqlServerAuthentication authentication
        ? authentication : SqlServerAuthentication.Windows;

    private SqlServerBackupSettings ReadSqlSettings(int timeout) => new()
    {
        Server = SqlBackupServer.Text.Trim(), ServerBackupDirectory = SqlServerDirectory.Text.Trim(),
        AgentReadDirectory = SqlAgentDirectory.Text.Trim(), TimeoutMinutes = timeout,
        Authentication = SelectedSqlAuthentication,
        CredentialKey = SelectedSqlAuthentication == SqlServerAuthentication.SqlServer ? sqlCredentialKey : null
    };

    private void SqlAuthentication_Changed(object sender, RoutedEventArgs e)
    {
        if (SqlCredentialsPanel is null) return;
        SqlCredentialsPanel.Visibility = SelectedSqlAuthentication == SqlServerAuthentication.SqlServer ? Visibility.Visible : Visibility.Collapsed;
        UpdateSqlCredentialStatus();
    }

    private void UpdateSqlCredentialStatus()
    {
        SqlCredentialStatus.Text = string.IsNullOrWhiteSpace(sqlCredentialKey)
            ? "Логин и пароль ещё не сохранены. Нажмите кнопку выше."
            : "Данные входа сохранены в Windows. Подключение проверяется отдельно после сохранения задания.";
    }

    private void SqlCredentials_Click(object sender, RoutedEventArgs e)
    {
        var key = Guid.NewGuid().ToString("N");
        var dialog = new FtpCredentialsWindow(key, new WindowsSqlServerCredentialStore().Save, "Логин и пароль MS SQL") { Owner = this };
        SqlCredentialsDialogOpened?.Invoke(dialog);
        if (dialog.ShowDialog() == true)
        {
            sqlCredentialKey = key; UpdateSqlCredentialStatus();
            EditorFieldChanged(sender, e);
        }
    }
}
