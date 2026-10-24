using System.Windows;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

public partial class ExternalMonitoringWindow
{
    private string? sqlCredentialKey;
    private string? sqlCredentialServer;
    private bool loadingSqlSource;
    internal Action<FtpCredentialsWindow>? SqlCredentialsDialogOpened { get; set; }
    private SqlServerAuthentication SelectedSqlAuthentication => SqlAuthentication.SelectedValue is SqlServerAuthentication value
        ? value : SqlServerAuthentication.Windows;

    private void SqlAuthentication_Changed(object sender, RoutedEventArgs e)
    {
        if (SqlCredentialsPanel is null) return;
        SqlCredentialsPanel.Visibility = SelectedSqlAuthentication == SqlServerAuthentication.SqlServer ? Visibility.Visible : Visibility.Collapsed;
        UpdateSqlCredentialStatus();
    }
    private void UpdateSqlCredentialStatus() => SqlCredentialStatus.Text = sqlCredentialKey is null
        ? "Логин и пароль не сохранены. Задайте их кнопкой выше."
        : "Данные входа сохранены в Windows. После сохранения источника нажмите «Проверить источник».";

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
        }
    }
}
