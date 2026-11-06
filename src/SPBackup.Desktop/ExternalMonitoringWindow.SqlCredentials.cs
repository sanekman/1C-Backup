using System.Windows;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class ExternalMonitoringWindow
{
    private string? sqlCredentialKey;
    private string? sqlCredentialServer;
    private bool loadingSqlSource;
    internal Action<FtpCredentialsWindow>? SqlCredentialsDialogOpened { get; set; }
    private SqlServerAuthentication SelectedSqlAuthentication => SqlAuthentication.SelectedValue is SqlServerAuthentication value
        ? value : SqlServerAuthentication.Windows;

    private void SqlTrustServerCertificate_Click(object sender, RoutedEventArgs e)
    {
        if (SqlTrustServerCertificate.IsChecked == true && !SqlCertificateWarning.Confirm(this))
            SqlTrustServerCertificate.IsChecked = false;
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
    private void UpdateSqlCredentialStatus() => SqlCredentialStatus.Text = sqlCredentialKey is null
        ? "Логин и пароль не сохранены. Задайте их кнопкой выше."
        : "Данные входа сохранены в Windows. После сохранения источника нажмите «Прочитать журнал копий».";

    private void SqlServer_Changed(object sender, RoutedEventArgs e)
    {
        if (loadingSqlSource || sqlCredentialKey is null || string.Equals(sqlCredentialServer, SqlServer.Text.Trim(), StringComparison.OrdinalIgnoreCase)) return;
        sqlCredentialKey = null; sqlCredentialServer = null; UpdateSqlCredentialStatus();
    }
    private void SqlCredentials_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SqlServer.Text)) { Status.Text = "Сначала укажите экземпляр MS SQL."; return; }
        var key = Guid.NewGuid().ToString("N");
        var dialog = new FtpCredentialsWindow(key, new WindowsSqlServerCredentialStore().Save, "Логин и пароль MS SQL") { Owner = this };
        SqlCredentialsDialogOpened?.Invoke(dialog);
        if (dialog.ShowDialog() == true)
        {
            sqlCredentialKey = key; sqlCredentialServer = SqlServer.Text.Trim(); UpdateSqlCredentialStatus();
            Status.Text = "Данные входа сохранены в Windows. Нажмите «Сохранить задание», затем «Прочитать журнал копий».";
        }
    }
}
