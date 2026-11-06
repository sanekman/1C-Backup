using System.Windows;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private GoogleDriveSettings? googleDriveSettings;
    private string? googleDriveFolder;
    private void UpdateGoogleDriveStatus()
    {
        if (GoogleDriveStatus is not null) GoogleDriveStatus.Text = googleDriveSettings is null
            ? "Аккаунт пока не подключён."
            : "Подключено · папка «" + googleDriveSettings.FolderName + "». Копии проверяются после отправки.";
    }
    private void GoogleDriveConnect_Click(object sender, RoutedEventArgs e)
    {
        if (!editorOpen || persisting) return;
        var dialog = new GoogleDriveWindow(googleDriveFolder, googleDriveSettings?.CredentialKey) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        googleDriveSettings = dialog.Settings; googleDriveFolder = dialog.FolderId; Destination.Text = googleDriveFolder;
        UpdateGoogleDriveStatus(); EditorFieldChanged(sender, e);
    }
}
