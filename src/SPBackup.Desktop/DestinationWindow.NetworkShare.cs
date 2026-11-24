using System.Windows;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class DestinationWindow
{
    private bool networkAccessEdited;
    internal Action<FtpCredentialsWindow>? NetworkShareCredentialsDialogOpened { get; set; }
    private void NetworkMode_Changed(object sender, RoutedEventArgs e)
    {
        if (NetworkCredentialsButton is null) return;
        NetworkCredentialsButton.Visibility = NetworkUseCredentials.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateNetworkStatus();
    }
    private void UpdateNetworkStatus()
    {
        if (NetworkCredentialStatus is null || NetworkCredentialsButton is null) return;
        var key = networkShareSettings?.CredentialKey;
        NetworkCredentialsButton.Content = string.IsNullOrEmpty(key)
            ? "Задать доступ к папке копий…" : "Изменить доступ к папке копий…";
        NetworkCredentialStatus.Text = NetworkUseCredentials.IsChecked != true
            ? "Вход в папку копий от имени исполнителя. Проверьте хранилище через агент или службу."
            : string.IsNullOrEmpty(key) ? "Отдельный доступ к папке копий не задан."
            : networkAccessEdited ? "Доступ задан в черновике. Применится после сохранения задания."
            : "Отдельный доступ задан. Проверьте хранилище под фактическим исполнителем.";
    }
    private void NetworkDestination_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (networkShareSettings is null || Storage.SelectedValue is not StorageKind.NetworkShare) return;
        try { if (string.Equals(NetworkShareSettings.Root(Destination.Text.Trim()), networkShareSettings.ShareRoot, StringComparison.OrdinalIgnoreCase)) return; }
        catch (ArgumentException) { }
        networkShareSettings = null; UpdateNetworkStatus();
    }
    private void NetworkCredentials_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var root = NetworkShareSettings.Root(Destination.Text.Trim());
            var key = "smb-" + Guid.NewGuid().ToString("N");
            var credentials = new WindowsNetworkShareCredentialStore();
            var dialog = new FtpCredentialsWindow(key, credentials.Save, "Доступ к папке копий на NAS") { Owner = this };
            dialog.DescribeStorageDraft(root, credentials.TryReadUsername(networkShareSettings?.CredentialKey));
            NetworkShareCredentialsDialogOpened?.Invoke(dialog);
            if (dialog.ShowDialog() != true) return;
            networkShareSettings = new() { ShareRoot = root, CredentialKey = key };
            networkAccessEdited = true;
            UpdateNetworkStatus(); Status.Text = "";
        }
        catch (Exception error) { Status.Text = error.Message; }
    }
}
