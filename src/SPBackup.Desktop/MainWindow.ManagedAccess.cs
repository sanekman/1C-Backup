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
        if (!ManagedAccessButton.IsEnabled || agentSettings?.Mode != AgentMode.Managed || Jobs.SelectedItem is not BackupJob { ManagedSecrets: { } required } job
            || !RequireConfigurationIdle()) return;
        var lease = AcquireEngineOrReport();
        if (lease is null) return;
        var archive = new WindowsArchivePasswordStore();
        var network = new WindowsNetworkShareCredentialStore();
        string? passwordKey = null;
        var keys = new Dictionary<int, string>();
        var saved = false;
        try
        {
            changingAgentConfiguration = true;
            UpdateRunControls();
            var requirements = required.NetworkDestinations.Select(index => "Логин и пароль NAS для " + job.StorageJobs[index].DestinationDirectory);
            if (required.ArchivePassword) requirements = requirements.Prepend("Пароль ZIP для новых архивов");
            var summary = $"Задание: {job.Name}\nИсточник: {job.Source}\n\n" + string.Join("\n", requirements)
                + "\n\nДоступ хранится на этом компьютере и применяется после подтверждения всех окон.";
            if (!(ManagedAccessConfirmation?.Invoke(job) ?? (MessageBox.Show(this, summary,
                "Настроить доступ серверного задания", MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK)))
            {
                Status.Text = "Настройка доступа отменена.";
                return;
            }
            if (required.ArchivePassword)
            {
                var dialog = new ArchivePasswordWindow { Owner = this };
                dialog.Instructions.Text = $"Задание «{job.Name}». Пароль применяется к новым архивам после подтверждения всех окон. Ранее созданные архивы открываются со своими паролями.";
                dialog.ApplyButton.Content = "Подтвердить пароль";
                ManagedArchiveDialogOpened?.Invoke(dialog);
                if (dialog.ShowDialog() != true)
                {
                    Status.Text = "Настройка доступа отменена.";
                    return;
                }
                passwordKey = dialog.NewKey;
            }
            foreach (var index in required.NetworkDestinations)
            {
                var target = job.StorageJobs[index];
                var key = "managed-smb-" + Guid.NewGuid().ToString("N");
                var dialog = new FtpCredentialsWindow(key, network.Save, $"Сетевой вход — хранилище {index + 1}") { Owner = this };
                dialog.DescribeJobDraft("Папка копий: " + target.DestinationDirectory,
                    network.TryReadUsername(target.NetworkShare?.CredentialKey), "Логин NAS",
                    "Доступ сохранится на этом компьютере после подтверждения всех окон. Затем запустите исполнителя и проверьте хранилища.");
                dialog.CredentialSaveButton.Content = "Подтвердить доступ";
                NetworkShareCredentialsDialogOpened?.Invoke(dialog);
                if (dialog.ShowDialog() != true)
                {
                    Status.Text = "Настройка доступа отменена.";
                    return;
                }
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
            Status.Text = "Локальный доступ сохранён. Включите агент или службу и нажмите «Проверить хранилища», чтобы проверить запись от имени исполнителя.";
        }
        catch (Exception)
        {
            Status.Text = saved ? "Доступ сохранён. Обновите список заданий, чтобы увидеть результат."
                : "Не удалось сохранить доступ. Проверьте настройки службы и повторите настройку выбранного задания.";
        }
        finally
        {
            if (!saved)
            {
                if (passwordKey is not null) try { archive.Delete(passwordKey); } catch (Exception) { }
                foreach (var key in keys.Values) try { network.Delete(key); } catch (Exception) { }
            }
            changingAgentConfiguration = false;
            lease.Dispose();
            UpdateRunControls();
        }
    }
}
