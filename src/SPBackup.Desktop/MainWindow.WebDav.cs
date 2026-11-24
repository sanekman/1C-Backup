using System.Windows;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private string? webDavCredentialKey, webDavCredentialSource, webDavUsername;
    private bool webDavAccessEdited;
    internal Action<FtpCredentialsWindow>? WebDavCredentialsDialogOpened { get; set; }

    private static string? WebDavAddress(string source)
    {
        var address = source.Trim();
        return WebDavSettings.ValidateSource(address).Count == 0 ? new Uri(address.TrimEnd('/') + "/").AbsoluteUri : null;
    }

    private string? WebDavKeyForSource => string.Equals(webDavCredentialSource, WebDavAddress(Source.Text), StringComparison.Ordinal)
        ? webDavCredentialKey : null;

    private void UpdateWebDavCredentialStatus()
    {
        if (WebDavCredentialStatus is null || Source is null) return;
        var available = WebDavKeyForSource is not null;
        WebDavCredentialsButton.Content = available ? "Изменить доступ…" : "Логин и пароль…";
        var login = webDavUsername is not null && available ? $"Логин: {webDavUsername}. " : "";
        WebDavCredentialStatus.Text = login + (available
            ? webDavAccessEdited ? "Доступ изменён в черновике." : "Учётные данные сохранены. Проверьте источник."
            : webDavCredentialKey is not null ? "Адрес изменён. Задайте доступ к новой папке."
            : "Доступ к папке не задан.");
    }

    private void WebDavCredentials_Click(object sender, RoutedEventArgs e)
    {
        var address = WebDavAddress(Source.Text);
        if (address is null)
        {
            RejectWizard("Укажите HTTPS-адрес папки WebDAV без логина, пароля, параметров и фрагмента.", 0, Source);
            return;
        }
        var key = Guid.NewGuid().ToString("N");
        var store = new WindowsWebDavCredentialStore();
        var dialog = new FtpCredentialsWindow(key, store.Save, "Доступ к папке WebDAV") { Owner = this };
        dialog.DescribeJobDraft("Папка WebDAV: " + address, WebDavKeyForSource is null ? null : webDavUsername, "Логин WebDAV",
            "Используйте пароль приложения, если его требует облако. Доступ применяется после сохранения задания. Затем нажмите «Проверить источник».");
        WebDavCredentialsDialogOpened?.Invoke(dialog);
        if (dialog.ShowDialog() != true) return;
        webDavCredentialKey = key;
        webDavCredentialSource = address;
        webDavUsername = store.TryReadUsername(key);
        webDavAccessEdited = true;
        UpdateWebDavCredentialStatus();
        EditorFieldChanged(sender, e);
    }
}
