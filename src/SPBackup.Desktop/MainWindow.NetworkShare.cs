using System.IO;
using System.Windows;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private NetworkShareSettings? networkShareSettings;
    internal Action<FtpCredentialsWindow>? NetworkShareCredentialsDialogOpened { get; set; }
    private NetworkShareSettings? ReadNetworkShareSettings() => Storage.SelectedValue is StorageKind.NetworkShare
        && NetworkUseCredentials.IsChecked == true ? networkShareSettings ?? new() : null;
    private void NetworkMode_Changed(object sender, RoutedEventArgs e)
    {
        if (NetworkCredentialsButton is null) return;
        NetworkCredentialsButton.Visibility = NetworkUseCredentials.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateNetworkStatus();
    }
    private void UpdateNetworkStatus()
    {
        if (NetworkCredentialStatus is null) return;
        NetworkCredentialStatus.Text = NetworkUseCredentials.IsChecked != true
            ? "Используется сетевой вход пользователя агента или службы Windows."
            : string.IsNullOrEmpty(networkShareSettings?.CredentialKey) ? "Задайте логин и пароль NAS. Они будут сохранены в Windows."
            : "Для этой сетевой папки задан отдельный вход. Сохраните задание и проверьте хранилища через службу.";
    }
    private void NetworkDestination_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!editorOpen || networkShareSettings is null || Storage.SelectedValue is not StorageKind.NetworkShare) return;
        try
        {
            if (string.Equals(NetworkShareSettings.Root(Destination.Text.Trim()), networkShareSettings.ShareRoot, StringComparison.OrdinalIgnoreCase)) return;
        }
        catch (ArgumentException) { }
        networkShareSettings = null; UpdateNetworkStatus();
    }
    private void NetworkCredentials_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var root = NetworkShareSettings.Root(Destination.Text.Trim());
            // A draft never changes the secret referenced by a running/saved job.
            var key = "smb-" + Guid.NewGuid().ToString("N");
            var dialog = new FtpCredentialsWindow(key, new WindowsNetworkShareCredentialStore().Save, "Логин и пароль NAS / сетевой папки") { Owner = this };
            dialog.BindingName.Text = "Укажите пользователя, которому на сетевом сервере разрешена запись в эту общую папку.";
            dialog.CredentialExplanation.Text = "Это сетевой пароль, а не PIN Windows. После сохранения доступа завершите сохранение задания.";
            NetworkShareCredentialsDialogOpened?.Invoke(dialog);
            if (dialog.ShowDialog() != true) return;
            networkShareSettings = new() { ShareRoot = root, CredentialKey = key }; UpdateNetworkStatus();
            EditorFieldChanged(sender, e);
        }
        catch (Exception error) { Status.Text = error.Message; }
    }
}
