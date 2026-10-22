using System.Text.Json;
using System.Windows;

namespace OneCBackup.Desktop;

public partial class MainWindow
{
    private string? editorBaseline;
    private bool editorContainsImportedDraft, editorClosePending;
    internal Action<UnsavedJobWindow>? UnsavedJobDialogOpened { get; set; }

    // Compare actual editable values, not routed change events: loading a job,
    // visiting wizard steps and asynchronous discovery also raise those events.
    private string CaptureEditorDraft() => JsonSerializer.Serialize(new
    {
        Text = new[] { JobName.Text, Source.Text, Destination.Text, TemporaryFolder.Text,
            OneCPlatformVersion.Text, OneCExecutable.Text, OneCTimeout.Text,
            SqlBackupServer.Text, SqlServerDirectory.Text, SqlAgentDirectory.Text, SqlBackupTimeout.Text,
            PgHost.Text, PgPort.Text, PgUsername.Text, PgDumpPath.Text, PgRestorePath.Text, PgTimeout.Text,
            WebDavTimeout.Text, FtpHost.Text, FtpPort.Text, FtpCredentialKey.Text,
            DailyAt.Text, KeepLast.Text, FullBackupEvery.Text },
        Selection = new[] { Kind.SelectedIndex, OneCMethodPicker.SelectedIndex, SqlAuthenticationPicker.SelectedIndex,
            FileBackupModePicker.SelectedIndex, FullBackupDayPicker.SelectedIndex, Storage.SelectedIndex, Schedule.SelectedIndex },
        Checks = new[] { AllowOpenOneCDatabase.IsChecked, AutoOneCPlatform.IsChecked, FtpTls.IsChecked,
            ArchiveCopies.IsChecked, EncryptArchive.IsChecked, Enabled.IsChecked,
            Mon.IsChecked, Tue.IsChecked, Wed.IsChecked, Thu.IsChecked, Fri.IsChecked, Sat.IsChecked, Sun.IsChecked },
        additionalDestinations, googleDriveSettings, googleDriveFolder, remoteGatewaySettings,
        archivePasswordKey, webDavCredentialKey, sqlCredentialKey
    });

    private void RememberEditorDraft()
    {
        editorBaseline = CaptureEditorDraft();
        // Duplicated/imported jobs already contain work before the first edit.
        editorContainsImportedDraft = editingId is null && !string.IsNullOrWhiteSpace(JobName.Text);
    }

    internal bool HasUnsavedJob => editorOpen && (editorContainsImportedDraft || editorBaseline != CaptureEditorDraft());

    private async Task<bool> ConfirmLeavingEditorAsync()
    {
        if (editorClosePending || persisting) return false;
        if (!HasUnsavedJob) return true;
        editorClosePending = true;
        try
        {
            ShowFromTray();
            var dialog = new UnsavedJobWindow { Owner = this };
            UnsavedJobDialogOpened?.Invoke(dialog);
            dialog.ShowDialog();
            if (dialog.Choice == UnsavedJobChoice.Save) return await SaveEditorAsync();
            return dialog.Choice == UnsavedJobChoice.Discard;
        }
        finally { editorClosePending = false; }
    }
}
