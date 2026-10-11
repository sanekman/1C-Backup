using System.Windows;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

public partial class MainWindow
{
    private string? webDavCredentialKey;
    internal Action<FtpCredentialsWindow>? WebDavCredentialsDialogOpened { get; set; }
    private void UpdateWebDavCredentialStatus()
    {
        WebDavCredentialStatus.Text = string.IsNullOrWhiteSpace(webDavCredentialKey) ? "Подключение ещё не настроено" : "Учётные данные сохранены в Windows";
    }
    private void WebDavCredentials_Click(object sender, RoutedEventArgs e)
    {
        // A new binding keeps other jobs using the previous credential unchanged.
        var key = Guid.NewGuid().ToString("N");
        var dialog = new FtpCredentialsWindow(key, new WindowsWebDavCredentialStore().Save, "Подключение WebDAV — пароль приложения") { Owner = this };
        WebDavCredentialsDialogOpened?.Invoke(dialog);
        if (dialog.ShowDialog() == true) { webDavCredentialKey = key; UpdateWebDavCredentialStatus(); }
    }
}
