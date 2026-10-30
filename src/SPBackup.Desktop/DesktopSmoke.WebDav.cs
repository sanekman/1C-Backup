using System.IO;
using System.Windows;
using System.Windows.Controls;
using SPBackup.Core;

namespace SPBackup.Desktop;

internal static partial class DesktopSmoke
{
    private static async Task VerifyWebDavWizardAsync(MainWindow window, Action<string> screenshot)
    {
        T Control<T>(string name) where T : FrameworkElement => (T)(window.FindName(name) ?? throw new InvalidOperationException(name));
        void Click(string name)
        {
            window.UpdateLayout(); var button = Control<Button>(name);
            if (!button.IsEnabled || !button.IsVisible) throw new InvalidOperationException("Кнопка WebDAV недоступна: " + name);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
        }
        var credentialStore = new WindowsWebDavCredentialStore(); string? credentialKey = null;
        try
        {
            await WaitUntil(() => window.SmokeReady && Control<Button>("NewJobButton").IsEnabled, "Мастер WebDAV не готов.");
            Click("JobsNavigation"); Click("NewJobButton");
            Control<TextBox>("JobName").Text = "WebDAV — документы";
            Control<ComboBox>("Kind").SelectedItem = DatabaseKind.WebDav;
            VerifyGuideLink(window, "WizardHelpButton", 15, "Скопировать папку из WebDAV или Nextcloud");
            Control<TextBox>("Source").Text = "http://dav.example/data/";
            Click("NextStepButton");
            if (!Control<TextBlock>("WizardError").IsVisible || !Control<TextBlock>("WizardError").Text.Contains("HTTPS"))
                throw new InvalidOperationException("Мастер принял незашифрованное подключение WebDAV.");
            Control<TextBox>("Source").Text = "https://dav.example/data/";
            Click("NextStepButton");
            if (!Control<TextBlock>("WizardError").Text.Contains("Логин и пароль"))
                throw new InvalidOperationException("Мастер WebDAV пропустил сохранение учётных данных.");
            window.WebDavCredentialsDialogOpened = dialog =>
            {
                credentialKey = ((TextBlock)dialog.FindName("BindingName")).Text.Split(':', 2)[1].Trim();
                dialog.Dispatcher.BeginInvoke(() =>
                {
                    ((TextBox)dialog.FindName("Username")).Text = "fixture-user";
                    ((PasswordBox)dialog.FindName("Password")).Password = "fixture-webdav-app-password";
                    ((Button)dialog.FindName("CredentialSaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
            };
            Click("WebDavCredentialsButton"); window.WebDavCredentialsDialogOpened = null;
            Control<ComboBox>("FileBackupModePicker").SelectedValue = FileBackupMode.Incremental;
            Control<TextBox>("FullBackupEvery").Text = "7";
            window.Width = 900; window.Height = 580; window.UpdateLayout();
            screenshot("webdav-source-layout");
            foreach (var name in new[] { "Source", "WebDavCredentialsButton", "FileBackupModePicker", "FullBackupEvery" })
            {
                var element = Control<FrameworkElement>(name); var scroll = Control<ScrollViewer>("EditorScroll");
                var point = element.TranslatePoint(new Point(), scroll);
                if (!element.IsVisible || point.Y < 0 || point.Y + element.ActualHeight > scroll.ActualHeight + 1)
                    throw new InvalidOperationException("Настройка WebDAV не видна в компактном мастере: " + name);
            }
            screenshot("webdav-source-compact"); Click("NextStepButton");
            var fixture = Path.GetDirectoryName(DesktopPaths.DataDirectory)!;
            Control<TextBox>("Destination").Text = Path.Combine(fixture, "dav-copies");
            Control<TextBox>("TemporaryFolder").Text = Path.Combine(fixture, "dav-HDD");
            Click("NextStepButton"); Control<CheckBox>("Enabled").IsChecked = false; Click("NextStepButton");
            if (!Control<TextBox>("WizardSummary").Text.Contains("WebDAV") || !Control<TextBox>("WizardSummary").Text.Contains("Инкремент"))
                throw new InvalidOperationException("Итог WebDAV потерял тип источника или файловый режим.");
            screenshot("webdav-review-compact"); Click("SaveButton");
            await WaitUntil(() => window.SmokeReady && Control<Border>("JobListCard").IsVisible, "WebDAV не сохранён.");
            var job = (await new JobStore(Path.Combine(DesktopPaths.DataDirectory, "jobs.json")).LoadAsync()).Single(job => job.Name == "WebDAV — документы");
            credentialKey = job.WebDav?.CredentialKey;
            if (job.Kind != DatabaseKind.WebDav || job.FileBackupMode != FileBackupMode.Incremental || job.FullBackupEvery != 7
                || job.WebDav?.TimeoutMinutes != 120 || !job.ArchiveCopies || credentialKey is null || job.Enabled)
                throw new InvalidOperationException("Настройки WebDAV не сохранились.");
            var credential = await credentialStore.GetAsync(credentialKey);
            if (credential.UserName != "fixture-user" || credential.Password != "fixture-webdav-app-password")
                throw new InvalidOperationException("WebDAV не сохранил пароль в защищённом хранилище Windows.");
            var json = await File.ReadAllTextAsync(Path.Combine(DesktopPaths.DataDirectory, "jobs.json"));
            if (json.Contains("fixture-webdav-app-password")) throw new InvalidOperationException("Пароль WebDAV попал в настройки.");
            Control<DataGrid>("Jobs").SelectedItem = Control<DataGrid>("Jobs").Items.OfType<BackupJob>().Single(item => item.Id == job.Id);
            Click("EditJobButton");
            if (!Control<TextBox>("Source").Text.StartsWith("https://") || !Control<TextBlock>("WebDavCredentialStatus").Text.Contains("сохранены"))
                throw new InvalidOperationException("Редактирование потеряло настройки WebDAV.");
            Click("CancelEditorButton");
        }
        finally { window.WebDavCredentialsDialogOpened = null; if (credentialKey is not null) credentialStore.Delete(credentialKey); }
    }
}
