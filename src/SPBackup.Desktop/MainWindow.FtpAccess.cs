using System.Windows;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private FtpSettings? ftpAccess;
    private bool ftpAccessEdited;
    internal Action<FtpCredentialsWindow>? FtpCredentialsDialogOpened { get; set; }

    private string? FtpKeyFor(int port) =>
        FtpAccessDraft.KeyFor(ftpAccess, FtpHost.Text, port, FtpTls.IsChecked == true);

    private void FtpAddress_Changed(object sender, RoutedEventArgs e) => UpdateFtpAccessStatus();

    private void UpdateFtpAccessStatus()
    {
        if (FtpAccessStatus is null) return;
        var key = int.TryParse(FtpPort.Text, out var port) ? FtpKeyFor(port) : null;
        FtpCredentialsButton.Content = key is null ? "Задать доступ FTP…" : "Изменить доступ FTP…";
        FtpAccessStatus.Text = FtpAccessDraft.Status(ftpAccess, key, ftpAccessEdited);
    }

    private void FtpCredentials_Click(object sender, RoutedEventArgs e)
    {
        if (!editorOpen || !FtpCredentialsButton.IsEnabled) return;
        var host = FtpHost.Text.Trim();
        var useTls = FtpTls.IsChecked == true;
        var error = FtpAccessDraft.ValidateAddress(host, FtpPort.Text, out var port);
        if (error is not null)
        {
            RejectWizard(error, 1, port is < 1 or > 65535 ? FtpPort : FtpHost);
            return;
        }

        // Each draft gets a new key, leaving the running or saved job untouched.
        var key = "ftp-" + Guid.NewGuid().ToString("N");
        var dialog = new FtpCredentialsWindow(key, ftpCredentials) { Owner = this };
        dialog.DescribeStorageDraft(FtpAccessDraft.AddressLabel(host, port, useTls), ftpCredentials.TryReadUsername(FtpKeyFor(port)));
        FtpCredentialsDialogOpened?.Invoke(dialog);
        if (dialog.ShowDialog() != true) return;
        ftpAccess = new FtpSettings { Host = host, Port = port, UseTls = useTls, CredentialKey = key };
        ftpAccessEdited = true;
        UpdateFtpAccessStatus();
        EditorFieldChanged(sender, e);
    }
}
