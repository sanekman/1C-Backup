using System.IO;
using System.Windows;
using System.Windows.Controls;
using SPBackup.Core;

namespace SPBackup.Desktop;

internal static partial class DesktopSmoke
{
    private static async Task VerifyFtpCredentialDraftsAsync(MainWindow window, Action<string> screenshot)
    {
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        void Click(string name)
        {
            var button = Control<Button>(name); button.BringIntoView(); window.UpdateLayout();
            Require(button.IsVisible && button.IsEnabled, "FTP action unavailable: " + name);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
        }
        var credentials = new WindowsFtpCredentialStore();
        var keys = new List<string>();
        var value = "FTP original fixture secret"; var cancel = false;
        void Fill(FtpCredentialsWindow dialog)
        {
            keys.Add(dialog.CredentialKey);
            dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(() => {
                if (cancel) { dialog.DialogResult = false; return; }
                ((TextBox)dialog.FindName("Username")).Text = "ftp-fixture";
                ((PasswordBox)dialog.FindName("Password")).Password = value;
                dialog.UpdateLayout();
                var save = (Button)dialog.FindName("CredentialSaveButton");
                Require(save.TransformToAncestor(dialog).Transform(new Point(0, save.ActualHeight)).Y <= dialog.ActualHeight,
                    "FTP credential Save button is outside its dialog.");
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
        }
        var path = Path.Combine(DesktopPaths.DataDirectory, "jobs.json");
        try
        {
            window.FtpCredentialsDialogOpened = Fill;
            Click("JobsNavigation"); Click("NewJobButton");
            Control<ComboBox>("Kind").SelectedItem = DatabaseKind.FilesAndFolders;
            Control<TextBox>("JobName").Text = "FTP credential draft fixture";
            Control<TextBox>("Source").Text = DesktopPaths.DataDirectory;
            Click("NextStepButton");
            Control<ComboBox>("Storage").SelectedValue = StorageKind.Ftp;
            Control<TextBox>("Destination").Text = "/copies";
            Control<TextBox>("FtpHost").Text = "ftp-fixture.invalid";
            using (new BackupEngineCoordinator(DesktopPaths.DataDirectory).Acquire())
            {
                await WaitUntil(() => window.SmokeReady && !Control<Button>("ArchivePasswordButton").IsEnabled,
                    "FTP fixture did not observe the executor lock.");
                Click("FtpCredentialsButton"); screenshot("ftp-login-with-agent");
                Require((await credentials.GetAsync(keys[^1])).Password == value, "FTP credentials were not saved in Windows.");
                Require(!(await new JobStore(path).LoadAsync()).Any(j => j.Name == "FTP credential draft fixture"),
                    "Entering FTP credentials published an unfinished job.");
            }
            Click("NextStepButton"); Control<ComboBox>("Schedule").SelectedValue = ScheduleKind.Manual;
            Control<CheckBox>("Enabled").IsChecked = false; Click("NextStepButton"); Click("SaveButton");
            await WaitUntil(() => window.SmokeReady && Control<Border>("JobListCard").IsVisible, "FTP job did not save.");
            var job = (await new JobStore(path).LoadAsync()).Single(j => j.Name == "FTP credential draft fixture");
            var originalKey = job.Ftp!.CredentialKey;
            Require(originalKey == keys[^1], "FTP draft binding did not reach the saved job.");
            Click("EditJobButton"); Click("NextStepButton");
            using (new BackupEngineCoordinator(DesktopPaths.DataDirectory).Acquire())
            {
                await WaitUntil(() => window.SmokeReady && !Control<Button>("ArchivePasswordButton").IsEnabled,
                    "FTP edit did not observe the executor lock.");
                cancel = true; Click("FtpCredentialsButton");
                Require(Control<TextBox>("FtpCredentialKey").Text == originalKey, "Cancel replaced the FTP binding.");
                cancel = false; value = "FTP replacement fixture secret"; Click("FtpCredentialsButton");
                Require(keys[^1] != originalKey, "Editing replaced the existing FTP Vault entry.");
                Require((await credentials.GetAsync(originalKey)).Password == "FTP original fixture secret", "FTP editing changed a running job's password.");
                Require((await new JobStore(path).LoadAsync()).Single(j => j.Id == job.Id).Ftp!.CredentialKey == originalKey,
                    "FTP editing changed the saved profile before Save.");
                screenshot("ftp-edit-isolated-draft");
            }
            Click("CancelEditorButton");
            Require((await new JobStore(path).LoadAsync()).Single(j => j.Id == job.Id).Ftp!.CredentialKey == originalKey,
                "Discarding the editor replaced the FTP binding.");
            // An additional storage editor must also isolate a shared credential reference.
            var extra = new DestinationWindow(credentials, new() { StorageKind = StorageKind.Ftp,
                DestinationDirectory = "/additional", Ftp = job.Ftp }) { Owner = window };
            extra.FtpCredentialsDialogOpened = Fill;
            extra.Loaded += (_, _) => extra.Dispatcher.BeginInvoke(() => {
                var button = FindCredentialsButton(extra);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                extra.DialogResult = false;
            });
            Require(extra.ShowDialog() == false, "Additional FTP editor did not cancel.");
            Require((await credentials.GetAsync(originalKey)).Password == "FTP original fixture secret",
                "Canceling additional FTP storage changed a shared password.");
            Require(!(await File.ReadAllTextAsync(path)).Contains(value), "FTP password leaked to the profile.");
        }
        finally
        {
            window.FtpCredentialsDialogOpened = null;
            foreach (var key in keys.Distinct()) credentials.Delete(key);
        }
    }
    private static Button FindCredentialsButton(DestinationWindow dialog)
    {
        // The additional-storage action is the named button in the FTP panel.
        return (Button)dialog.FindName("FtpCredentialsButton");
    }
}
