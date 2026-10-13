using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

public partial class MainWindow
{
    private readonly ObservableCollection<BackupDestination> additionalDestinations = [];

    private void AdditionalStorage_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (EditStorageButton is null || RemoveStorageButton is null) return;
        EditStorageButton.IsEnabled = RemoveStorageButton.IsEnabled = AdditionalStorageList.SelectedItem is BackupDestination;
    }

    private void AddStorage_Click(object sender, RoutedEventArgs e)
    {
        if (!editorOpen || persisting) return;
        if (additionalDestinations.Count >= 7)
        { RejectWizard("Можно указать не более 8 хранилищ, включая основное.", 1, AddStorageButton); return; }
        var dialog = new DestinationWindow(ftpCredentials) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is not null)
        { additionalDestinations.Add(dialog.Result); AdditionalStorageList.SelectedItem = dialog.Result; EditorFieldChanged(sender, e); }
    }

    private void EditStorage_Click(object sender, RoutedEventArgs e)
    {
        if (!editorOpen || persisting || AdditionalStorageList.SelectedItem is not BackupDestination destination) return;
        var dialog = new DestinationWindow(ftpCredentials, destination) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is not null)
        { additionalDestinations[additionalDestinations.IndexOf(destination)] = dialog.Result; AdditionalStorageList.SelectedItem = dialog.Result; EditorFieldChanged(sender, e); }
    }

    private void RemoveStorage_Click(object sender, RoutedEventArgs e)
    {
        if (!editorOpen || persisting || AdditionalStorageList.SelectedItem is not BackupDestination destination) return;
        additionalDestinations.Remove(destination);
        EditorFieldChanged(sender, e);
    }

    private static bool CanExtractCopy(BackupRecord record, BackupJob job)
    {
        if (job.Kind != DatabaseKind.OneCFile || job.OneCMethod != OneCBackupMethod.FileArchive) return false;
        try { return BackupCopyVerifier.GetStorageJob(record, job).StorageKind is StorageKind.LocalDirectory or StorageKind.NetworkShare; }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException) { return false; }
    }
}
