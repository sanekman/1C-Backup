using System.IO;
using System.Windows;
using System.Windows.Controls;
using SPBackup.Core;

namespace SPBackup.Desktop;

internal static partial class DesktopSmoke
{
    private static async Task VerifyNetworkShareCredentialsAsync(MainWindow window, Action<string> screenshot)
    {
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Click(string name)
        {
            var button = Control<Button>(name); button.BringIntoView(); window.UpdateLayout();
            if (!button.IsEnabled || !button.IsVisible) throw new InvalidOperationException("NAS action unavailable: " + name);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
        }
        void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        const string secret = "NAS UI fixture password, never a real password";
        var keys = new List<string>(); var credentials = new WindowsNetworkShareCredentialStore();
        void Fill(FtpCredentialsWindow dialog)
        {
            keys.Add(dialog.CredentialKey);
            dialog.Dispatcher.BeginInvoke(() => {
                ((TextBox)dialog.FindName("Username")).Text = @"NAS\fixture-backup";
                ((PasswordBox)dialog.FindName("Password")).Password = secret;
                dialog.UpdateLayout();
                var save = (Button)dialog.FindName("CredentialSaveButton");
                Require(save.TransformToAncestor(dialog).Transform(new Point(0, save.ActualHeight)).Y <= dialog.ActualHeight,
                    "The NAS credential explanation pushed its Save button outside the dialog.");
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
        }
        try
        {
            Click("JobsNavigation"); Click("NewJobButton");
            Control<ComboBox>("Kind").SelectedItem = DatabaseKind.FilesAndFolders;
            Control<TextBox>("JobName").Text = "NAS explicit login UI fixture";
            Control<TextBox>("Source").Text = DesktopPaths.DataDirectory; Click("NextStepButton");
            Control<ComboBox>("Storage").SelectedValue = StorageKind.NetworkShare;
            Control<TextBox>("Destination").Text = @"\\nas-fixture\backups\company";
            Control<CheckBox>("NetworkUseCredentials").IsChecked = true;
            Click("NextStepButton"); Require(Control<TextBlock>("WizardError").IsVisible, "NAS login was optional after explicit authentication was selected.");
            window.NetworkShareCredentialsDialogOpened = Fill; Click("NetworkCredentialsButton");
            Control<TextBox>("Destination").Text = @"\\nas-fixture\other-share\company";
            Click("NextStepButton"); Require(Control<TextBlock>("WizardError").IsVisible, "A NAS password followed an unrelated share.");
            Click("NetworkCredentialsButton"); screenshot("network-share-explicit-login");
            Click("NextStepButton"); Control<ComboBox>("Schedule").SelectedValue = ScheduleKind.Manual;
            Control<CheckBox>("Enabled").IsChecked = false; Click("NextStepButton"); Click("SaveButton");
            await WaitUntil(() => window.SmokeReady && Control<Border>("JobListCard").IsVisible, "The NAS job did not save.");
            var path = Path.Combine(DesktopPaths.DataDirectory, "jobs.json");
            var job = (await new JobStore(path).LoadAsync()).Single(j => j.Name == "NAS explicit login UI fixture");
            Require(job.NetworkShare?.ShareRoot == @"\\nas-fixture\other-share" && job.NetworkShare.CredentialKey == keys[^1], "The saved NAS binding differs from the editor.");
            Require((await credentials.GetAsync(keys[^1])).Password == secret, "The NAS password was not saved in Windows.");
            Require(!(await File.ReadAllTextAsync(path)).Contains(job.Id.ToString()), "A legacy executor could run the NAS job without its login.");
            Require(!(await File.ReadAllTextAsync(path + ".protected.json")).Contains(secret), "A NAS password leaked into JSON.");
            Click("EditJobButton"); Click("NextStepButton");
            Require(Control<CheckBox>("NetworkUseCredentials").IsChecked == true, "Reopening removed explicit NAS authentication.");
            // A different coordinator holds the actual executor lock, just as
            // the background agent does. Do not fake the window's busy flags.
            using (new BackupEngineCoordinator(DesktopPaths.DataDirectory).Acquire())
            {
                await WaitUntil(() => window.SmokeReady && !Control<Button>("FtpCredentialsButton").IsEnabled,
                    "The window did not detect a running background executor.");
                Require(Control<Button>("NetworkCredentialsButton").IsEnabled, "The background executor blocked NAS credential editing.");
                var previousKey = job.NetworkShare!.CredentialKey;
                Click("NetworkCredentialsButton");
                Require(keys[^1] != previousKey, "Editing NAS credentials replaced the running job's secret.");
                Require((await new JobStore(path).LoadAsync()).Single(j => j.Id == job.Id).NetworkShare!.CredentialKey == previousKey,
                    "A NAS credential draft changed the saved job before confirmation.");
                Require((await credentials.GetAsync(previousKey)).Password == secret, "A NAS draft removed the running job's password.");
                screenshot("network-share-edit-with-agent");
            }
            await WaitUntil(() => window.SmokeReady && Control<Button>("FtpCredentialsButton").IsEnabled,
                "The window did not observe the background executor stopping.");
            Click("NextStepButton"); Click("NextStepButton"); Click("SaveButton");
            await WaitUntil(() => window.SmokeReady && Control<Border>("JobListCard").IsVisible, "NAS changes could not save after the executor stopped.");
            var originalKey = job.NetworkShare!.CredentialKey;
            job = (await new JobStore(path).LoadAsync()).Single(j => j.Id == job.Id);
            Require(job.NetworkShare!.CredentialKey == keys[^1], "The new NAS login was not applied after saving.");
            Require((await credentials.GetAsync(originalKey)).Password == secret, "Saving removed the previous job's password.");
            var extra = new DestinationWindow(new WindowsFtpCredentialStore()) { Owner = window };
            extra.NetworkShareCredentialsDialogOpened = Fill;
            _ = extra.Dispatcher.BeginInvoke(() => {
                ((ComboBox)extra.FindName("Storage")).SelectedValue = StorageKind.NetworkShare;
                ((TextBox)extra.FindName("Destination")).Text = @"\\second-nas\copies\company";
                ((CheckBox)extra.FindName("NetworkUseCredentials")).IsChecked = true;
                ((Button)extra.FindName("NetworkCredentialsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ((Button)extra.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            Require(extra.ShowDialog() == true && extra.Result?.NetworkShare?.ShareRoot == @"\\second-nas\copies", "Additional NAS login was not saved.");
            Require(extra.Result!.NetworkShare!.CredentialKey != job.NetworkShare!.CredentialKey, "Independent shares reused the same draft password binding.");
        }
        finally { window.NetworkShareCredentialsDialogOpened = null; foreach (var key in keys) credentials.Delete(key); }
    }
}
