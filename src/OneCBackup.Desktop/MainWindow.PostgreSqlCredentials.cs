using System.Windows;
using System.Windows.Controls;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

public partial class MainWindow
{
    private string? postgreSqlCredentialKey;
    internal Action<FtpCredentialsWindow>? PostgreSqlCredentialsDialogOpened { get; set; }
    private bool UseSavedPostgreSqlPassword => PgAuthenticationPicker.SelectedIndex == 0;
    private PostgreSqlBackupSettings ReadPostgreSqlSettings(int port, int timeout) => new()
    {
        Host = PgHost.Text.Trim(), Port = port, Username = PgUsername.Text.Trim(),
        PgDumpPath = PgDumpPath.Text.Trim(), PgRestorePath = PgRestorePath.Text.Trim(), TimeoutMinutes = timeout,
        CredentialKey = UseSavedPostgreSqlPassword ? postgreSqlCredentialKey : null
    };
    private void PgAuthentication_Changed(object sender, RoutedEventArgs e)
    {
        if (PgCredentialsPanel is null) return;
        PgCredentialsPanel.Visibility = UseSavedPostgreSqlPassword ? Visibility.Visible : Visibility.Collapsed;
        PgExternalAuthenticationHint.Visibility = UseSavedPostgreSqlPassword ? Visibility.Collapsed : Visibility.Visible;
        UpdatePostgreSqlCredentialStatus();
    }
    private void UpdatePostgreSqlCredentialStatus()
    {
        PgCredentialStatus.Text = postgreSqlCredentialKey is null
            ? "Пароль ещё не сохранён. Укажите пользователя и нажмите «Задать пароль»."
            : "Пароль сохранён в Windows. Подключение проверяется отдельно после сохранения задания.";
    }
    private void PgConnection_Changed(object sender, TextChangedEventArgs e)
    {
        if (!editorOpen || postgreSqlCredentialKey is null) return;
        postgreSqlCredentialKey = null;
        UpdatePostgreSqlCredentialStatus();
        PgCredentialStatus.Text = "Подключение изменено. Задайте пароль для выбранного сервера и пользователя заново.";
    }
    private void PgCredentials_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PgUsername.Text))
        { RejectWizard("Сначала укажите пользователя PostgreSQL.", 0, PgUsername); return; }
        var key = Guid.NewGuid().ToString("N");
        var dialog = new FtpCredentialsWindow(key, new WindowsPostgreSqlCredentialStore().Save, "Пароль PostgreSQL") { Owner = this };
        ((TextBlock)dialog.FindName("BindingName")).Text = "Сервер: " + PgHost.Text.Trim() + ":" + PgPort.Text.Trim() + " · база: " + Source.Text.Trim();
        var username = (TextBox)dialog.FindName("Username"); username.Text = PgUsername.Text.Trim(); username.IsReadOnly = true;
        PostgreSqlCredentialsDialogOpened?.Invoke(dialog);
        if (dialog.ShowDialog() == true)
        {
            postgreSqlCredentialKey = key; UpdatePostgreSqlCredentialStatus(); EditorFieldChanged(sender, e);
        }
    }
}
