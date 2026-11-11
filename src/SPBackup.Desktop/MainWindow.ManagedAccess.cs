using System.Windows;
using SPBackup.Core;

namespace SPBackup.Desktop;
public partial class MainWindow
{
    internal Func<BackupJob, bool>? ManagedAccessConfirmation { get; set; }
    internal Action<ArchivePasswordWindow>? ManagedArchiveDialogOpened { get; set; }
    private async void ManagedAccess_Click(object sender, RoutedEventArgs e) => await ConfigureManagedAccessAsync();
    private async Task ConfigureManagedAccessAsync()
    {
        if (agentSettings?.Mode != AgentMode.Managed || Jobs.SelectedItem is not BackupJob { ManagedSecrets: { } required } job
            || !RequireConfigurationIdle()) return;
        using var lease = AcquireEngineOrReport();
        if (lease is null) return;
        var archive = new WindowsArchivePasswordStore();
        var network = new WindowsNetworkShareCredentialStore();
        string? passwordKey = null;
        var keys = new Dictionary<int, string>();
        var saved = false;
        try
        {
            if (!(ManagedAccessConfirmation?.Invoke(job) ?? (MessageBox.Show(this, $"Задание: {job.Name}\nИсточник: {job.Source}\nХранилища:\n" + string.Join("\n", job.StorageJobs.Select(j => j.DestinationDirectory))
                + "\n\nДоступ будет привязан к этим адресам. Пароли сохранятся в Windows и будут доступны настроенной службе SP Backup. Продолжить?",
                "Доступ серверного задания", MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK))) return;
            if (required.ArchivePassword)
            {
                var dialog = new ArchivePasswordWindow { Owner = this };
                ManagedArchiveDialogOpened?.Invoke(dialog);
                if (dialog.ShowDialog() != true) return;
                passwordKey = dialog.NewKey;
            }
            foreach (var index in required.NetworkDestinations)
            {
                var target = job.StorageJobs[index];
                var key = "managed-smb-" + Guid.NewGuid().ToString("N");
                var dialog = new FtpCredentialsWindow(key, network.Save, $"Сетевой вход — хранилище {index + 1}")
                    { Owner = this, SizeToContent = SizeToContent.Height };
                dialog.BindingName.Text = target.DestinationDirectory;
                dialog.CredentialExplanation.Text = "Логин и пароль будут сохранены в Windows для этого задания и общей папки. Панель сервера их не получает.";
                NetworkShareCredentialsDialogOpened?.Invoke(dialog);
                if (dialog.ShowDialog() != true) return;
                keys.Add(index, key);
            }
            var profile = new ManagedAgentProfile(DesktopPaths.DataDirectory, agentSettings);
            var resolved = job with { ArchivePasswordKey = passwordKey,
                NetworkShare = keys.TryGetValue(0, out var primary) ? new() { ShareRoot = NetworkShareSettings.Root(job.DestinationDirectory), CredentialKey = primary } : null,
                AdditionalDestinations = job.AdditionalDestinations?.Select((d, i) => d with { NetworkShare = keys.TryGetValue(i + 1, out var key)
                    ? new() { ShareRoot = NetworkShareSettings.Root(d.DestinationDirectory), CredentialKey = key } : null }).ToArray() };
            await WindowsFtpCredentialStore.ExportProfileForServiceAsync([resolved], []);
            await profile.Bindings.SaveAsync(job, new(job.Id, ManagedJobBindings.Scope(job), passwordKey, keys));
            saved = true;
            await LoadEffectiveJobsAsync();
            Jobs.SelectedItem = jobs.FirstOrDefault(j => j.Id == job.Id);
            Status.Text = "Доступ сохранён в Windows. Включите агент/службу и проверьте хранилища. Серверный план и прежние копии сохранены.";
        }
        catch (Exception) { Status.Text = "Доступ не применён полностью. Проверьте настройки службы и повторите настройку доступа выбранного задания."; }
        finally
        {
            if (!saved)
            {
                if (passwordKey is not null) try { archive.Delete(passwordKey); } catch (Exception) { }
                foreach (var key in keys.Values) try { network.Delete(key); } catch (Exception) { }
            }
        }
    }
}
