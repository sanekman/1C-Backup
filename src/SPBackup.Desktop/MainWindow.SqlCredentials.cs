using System.Windows;
using SPBackup.Core;

namespace SPBackup.Desktop;

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
        TrustServerCertificate = SqlTrustServerCertificate.IsChecked == true,
        Authentication = SelectedSqlAuthentication,
        CredentialKey = SelectedSqlAuthentication == SqlServerAuthentication.SqlServer ? sqlCredentialKey : null
    };

    private void SqlTrustServerCertificate_Click(object sender, RoutedEventArgs e)
    {
        if (SqlTrustServerCertificate.IsChecked == true && !SqlCertificateWarning.Confirm(this))
            SqlTrustServerCertificate.IsChecked = false;
        EditorFieldChanged(sender, e);
    }

    private void SqlAuthentication_Changed(object sender, RoutedEventArgs e)
    {
        if (SqlCredentialsPanel is null) return;
        SqlCredentialsPanel.Visibility = SelectedSqlAuthentication == SqlServerAuthentication.SqlServer ? Visibility.Visible : Visibility.Collapsed;
        if (SqlAuthenticationHelp is not null)
            SqlAuthenticationHelp.Text = SelectedSqlAuthentication == SqlServerAuthentication.SqlServer
                ? "Укажите отдельный логин MS SQL кнопкой ниже. Это не пароль Windows и не пароль пользователя 1С. Данные сохраняются в защищённом хранилище Windows."
                : "Вход от имени пользователя, который запускает агент. При работе службой — от имени учётной записи службы. Этой записи нужен доступ к MS SQL.";
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
