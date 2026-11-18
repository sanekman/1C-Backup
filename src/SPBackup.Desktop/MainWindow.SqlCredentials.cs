using System.Windows;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private string? sqlCredentialKey, sqlCredentialServer, sqlUsername;
    private bool sqlCredentialsChanged;
    private string? SqlCredentialForServer => string.Equals(sqlCredentialServer, SqlBackupServer.Text.Trim(),
        StringComparison.OrdinalIgnoreCase) ? sqlCredentialKey : null;
    internal Action<FtpCredentialsWindow>? SqlCredentialsDialogOpened { get; set; }
    private SqlServerAuthentication SelectedSqlAuthentication => SqlAuthenticationPicker.SelectedValue is SqlServerAuthentication authentication
        ? authentication : SqlServerAuthentication.Windows;

    private SqlServerBackupSettings ReadSqlSettings(int timeout) => new()
    {
        Server = SqlBackupServer.Text.Trim(), ServerBackupDirectory = SqlServerDirectory.Text.Trim(),
        AgentReadDirectory = SqlAgentDirectory.Text.Trim(), TimeoutMinutes = timeout,
        TrustServerCertificate = SqlTrustServerCertificate.IsChecked == true,
        Authentication = SelectedSqlAuthentication,
        CredentialKey = SelectedSqlAuthentication == SqlServerAuthentication.SqlServer ? SqlCredentialForServer : null
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
        UpdateSqlCredentialStatus();
        EditorFieldChanged(sender, e);
    }

    private void SqlServer_Changed(object sender, RoutedEventArgs e)
    {
        UpdateSqlCredentialStatus();
        EditorFieldChanged(sender, e);
    }

    private void UpdateSqlCredentialStatus()
    {
        if (SqlCredentialStatus is null || SqlAuthenticationHelp is null || SqlBackupServer is null) return;
        UpdateSqlAuthenticationHelp();
        var available = SqlCredentialForServer is not null;
        SqlCredentialsButton.Content = available ? "Изменить доступ MS SQL…" : "Задать логин и пароль MS SQL…";
        var account = sqlUsername is null ? "" : $"Логин MS SQL: {sqlUsername}. ";
        SqlCredentialStatus.Text = available
            ? account + (sqlCredentialsChanged ? "Данные входа изменены в черновике." : "Данные входа сохранены. Проверьте источник после сохранения задания.")
            : sqlCredentialKey is not null ? "Сервер изменён. Задайте логин и пароль для нового сервера."
            : "Логин и пароль ещё не заданы.";
    }

    private void UpdateSqlAuthenticationHelp()
    {
        if (SqlAuthenticationHelp is null) return;
        SqlAuthenticationHelp.Text = SelectedSqlAuthentication == SqlServerAuthentication.SqlServer
            ? "Для этого сервера используется учётная запись MS SQL."
            : $"При запуске из окна или через фоновый агент используется {Environment.UserDomainName}\\{Environment.UserName}. "
                + (serviceExecutionAccount is { Length: > 0 } account ? $"Учётная запись службы: {account}."
                    : "Учётная запись службы задаётся в «Настройки → Фоновая работа».")
                + " В отчёте проверки будет указан фактический исполнитель.";
    }

    private void SqlCredentials_Click(object sender, RoutedEventArgs e)
    {
        var server = SqlBackupServer.Text.Trim();
        var error = server.Length == 0 ? "Сначала укажите сервер или экземпляр MS SQL."
            : BackupSetupGuidance.DatabaseServerError(DatabaseKind.SqlServer, server);
        if (error is not null)
        {
            RejectWizard(error, 0, SqlBackupServer);
            return;
        }
        var key = Guid.NewGuid().ToString("N");
        var store = new WindowsSqlServerCredentialStore();
        var dialog = new FtpCredentialsWindow(key, store.Save, "Логин и пароль MS SQL") { Owner = this };
        dialog.DescribeJobDraft("Сервер MS SQL: " + server, SqlCredentialForServer is null ? null : sqlUsername, "Логин MS SQL",
            "Пароль хранится в Windows. Доступ применяется после сохранения задания. Затем нажмите «Проверить источник».");
        SqlCredentialsDialogOpened?.Invoke(dialog);
        if (dialog.ShowDialog() == true)
        {
            sqlCredentialKey = key;
            sqlCredentialServer = server;
            sqlUsername = store.TryReadUsername(key);
            sqlCredentialsChanged = true;
            UpdateSqlCredentialStatus();
            EditorFieldChanged(sender, e);
        }
    }
}
