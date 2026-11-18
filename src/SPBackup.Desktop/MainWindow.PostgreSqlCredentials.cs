using System.Windows;
using System.Windows.Controls;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private PostgreSqlBackupSettings? postgreSqlAccess;
    private bool postgreSqlAccessEdited;
    private string? PostgreSqlKeyFor(int port) => postgreSqlAccess is { } access && access.Port == port
        && string.Equals(access.Host.Trim(), PgHost.Text.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(access.Username.Trim(), PgUsername.Text.Trim(), StringComparison.Ordinal)
        ? access.CredentialKey : null;
    internal Action<FtpCredentialsWindow>? PostgreSqlCredentialsDialogOpened { get; set; }
    private bool UseSavedPostgreSqlPassword => PgAuthenticationPicker.SelectedIndex == 0;
    private PostgreSqlBackupSettings ReadPostgreSqlSettings(int port, int timeout) => new()
    {
        Host = PgHost.Text.Trim(), Port = port, Username = PgUsername.Text.Trim(),
        PgDumpPath = PgDumpPath.Text.Trim(), PgRestorePath = PgRestorePath.Text.Trim(), TimeoutMinutes = timeout,
        Tls = PgTlsRequired.IsChecked == true ? new PostgreSqlTlsSettings
        { RootCertificatePath = string.IsNullOrWhiteSpace(PgTlsRootCertificate.Text) ? null : PgTlsRootCertificate.Text.Trim() } : null,
        CredentialKey = UseSavedPostgreSqlPassword ? PostgreSqlKeyFor(port) : null
    };
    private void BrowsePostgreSqlCertificate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Корневой сертификат PostgreSQL", Filter = "Сертификаты PEM|*.pem;*.crt|Все файлы|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) PgTlsRootCertificate.Text = dialog.FileName;
    }
    private void PgAuthentication_Changed(object sender, RoutedEventArgs e)
    {
        if (PgCredentialsPanel is null) return;
        PgCredentialsPanel.Visibility = UseSavedPostgreSqlPassword ? Visibility.Visible : Visibility.Collapsed;
        PgExternalAuthenticationHint.Visibility = UseSavedPostgreSqlPassword ? Visibility.Collapsed : Visibility.Visible;
        UpdatePostgreSqlCredentialStatus();
        EditorFieldChanged(sender, e);
    }
    private void UpdatePostgreSqlCredentialStatus()
    {
        if (PgCredentialStatus is null) return;
        var key = int.TryParse(PgPort.Text, out var port) ? PostgreSqlKeyFor(port) : null;
        PgCredentialsButton.Content = key is null ? "Задать пароль PostgreSQL…" : "Изменить пароль PostgreSQL…";
        PgCredentialStatus.Text = key is not null
            ? postgreSqlAccessEdited ? "Пароль изменён в черновике." : "Пароль сохранён. Подключение проверяется отдельно."
            : postgreSqlAccess?.CredentialKey is not null ? "Подключение изменено. Задайте пароль для этого сервера и пользователя."
            : "Пароль не задан.";
    }

    private void PgConnection_Changed(object sender, TextChangedEventArgs e)
    {
        UpdatePostgreSqlCredentialStatus();
        EditorFieldChanged(sender, e);
    }

    private void PgCredentials_Click(object sender, RoutedEventArgs e)
    {
        var host = PgHost.Text.Trim();
        var username = PgUsername.Text.Trim();
        var hostError = host.Length == 0 ? "Укажите сервер PostgreSQL." : BackupSetupGuidance.DatabaseServerError(DatabaseKind.PostgreSql, host);
        if (hostError is not null) { RejectWizard(hostError, 0, PgHost); return; }
        if (!int.TryParse(PgPort.Text, out var port) || port is < 1 or > 65535)
        { RejectWizard("Укажите порт PostgreSQL от 1 до 65535.", 0, PgPort); return; }
        if (username.Length == 0) { RejectWizard("Укажите пользователя PostgreSQL.", 0, PgUsername); return; }
        var key = Guid.NewGuid().ToString("N");
        var dialog = new FtpCredentialsWindow(key, new WindowsPostgreSqlCredentialStore().Save, "Пароль PostgreSQL") { Owner = this };
        var address = host.Contains(':') ? $"[{host.Trim('[', ']')}]:{port}" : $"{host}:{port}";
        dialog.DescribeJobDraft($"Сервер PostgreSQL: {address} · база: {Source.Text.Trim()}", username, "Пользователь PostgreSQL",
            "Пароль применяется после сохранения задания. Затем нажмите «Проверить источник» от имени нужного исполнителя.");
        ((TextBox)dialog.FindName("Username")).IsReadOnly = true;
        PostgreSqlCredentialsDialogOpened?.Invoke(dialog);
        if (dialog.ShowDialog() == true)
        {
            postgreSqlAccess = new() { Host = host, Port = port, Username = username, CredentialKey = key };
            postgreSqlAccessEdited = true;
            UpdatePostgreSqlCredentialStatus();
            EditorFieldChanged(sender, e);
        }
    }
}
