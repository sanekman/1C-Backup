using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private bool serviceManaging;
    private string ServiceExecutable => Path.Combine(AppContext.BaseDirectory, "SPBackup.Agent.exe");
    private WindowsAgentServiceInfo? OwnService()
    {
        var info = WindowsAgentService.QueryInfo();
        return info?.Matches(ServiceExecutable, DesktopPaths.DataDirectory) == true ? info : null;
    }
    private async Task InitializeServiceAsync()
    {
        if (SmokeMode) { RefreshService(); return; }
        var currentAccount = WindowsIdentity.GetCurrent().Name;
        ServiceAccount.Text = currentAccount;
        try
        {
            var users = await Task.Run(WindowsServiceAccounts.LocalUsers);
            ServiceAccount.ItemsSource = new[] { currentAccount }.Concat(users).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            ServiceAccount.Text = currentAccount;
            ServiceAccountHint.Text = "Список содержит локальных пользователей. Доменное имя можно ввести вручную.";
        }
        catch (Exception error) { ServiceAccountHint.Text = "Список недоступен. Имя можно ввести вручную. " + error.Message; }
        if (await WindowsServiceProfile.LoadAsync(DesktopPaths.DataDirectory) is { } profile)
        {
            profile.AssertCurrentUser();
            WindowsFtpCredentialStore.SetServiceCache(new(DesktopPaths.DataDirectory, profile.OwnerSid, profile.EffectiveServiceSid));
            try { ServiceAccount.Text = ((NTAccount)new SecurityIdentifier(profile.EffectiveServiceSid).Translate(typeof(NTAccount))).Value; }
            catch { ServiceAccountHint.Text = "Сохранённый пользователь недоступен. Укажите имя вручную."; }
        }
        RefreshService();
    }
    private void RefreshService()
    {
        if (SmokeMode) { ServiceStatus.Text = "Управление службой недоступно в тестовом профиле."; return; }
        try
        {
            var info = WindowsAgentService.QueryInfo();
            var owned = info?.Matches(ServiceExecutable, DesktopPaths.DataDirectory) == true;
            automationService = info is null ? AutomationService.None : !owned ? AutomationService.OtherProfile
                : info.State switch { 1 => AutomationService.Stopped, 2 => AutomationService.Starting, 3 => AutomationService.Stopping,
                    4 => AutomationService.Running, _ => AutomationService.Unavailable };
            ServiceStatus.Text = info is null ? "Служба не установлена." : owned ? "Служба: " + info.Description + " · " + info.Account
                : "Служба установлена для другой папки программы или профиля.";
            if (info is null && !ApplicationUpdateHandoff.IsInstalled())
                ServiceStatus.Text += " Для установки службы используйте установщик Windows.";
            InstallServiceButton.IsEnabled = ready && configurationReady && !ConfigurationWorkInProgress && info is null && ApplicationUpdateHandoff.IsInstalled();
            RemoveServiceButton.IsEnabled = ready && !serviceManaging && owned && info!.State == 1;
            StartServiceButton.IsEnabled = ready && configurationReady && !ConfigurationWorkInProgress && !backgroundBusy && owned && info!.State == 1;
            StopServiceButton.IsEnabled = ready && !serviceManaging && owned && info!.State == 4;
        }
        catch (Exception error) { automationService = AutomationService.Unavailable; ServiceStatus.Text = error.Message; }
    }
    private async void InstallService_Click(object sender, RoutedEventArgs e) => await ManageServiceAsync("install");
    private async void RemoveService_Click(object sender, RoutedEventArgs e) => await ManageServiceAsync("remove");
    private async Task ManageServiceAsync(string action)
    {
        if (!ready || ConfigurationWorkInProgress || SmokeMode) return;
        serviceManaging = true; ServiceAccount.IsEnabled = ServicePassword.IsEnabled = false; UpdateRunControls();
        try
        {
            if (!ApplicationUpdateHandoff.IsInstalled()) throw new InvalidOperationException("Для службы установите программу установщиком Windows.");
            if (backgroundBusy || activeRun is not null || schedulingTask is not null || manualPending || persisting)
                throw new InvalidOperationException("Сначала остановите автоматическое копирование и дождитесь окончания операций.");
            if (action == "install") await ValidateExecutionConnectionAsync();
            var identity = WindowsIdentity.GetCurrent(); var sid = identity.User!.Value;
            var account = identity.Name;
            if (action == "install")
            {
                if (string.IsNullOrEmpty(ServicePassword.Password)) throw new InvalidOperationException("Введите пароль Windows, а не PIN.");
                var enteredAccount = ServiceAccount.Text;
                var selected = await Task.Run(() => WindowsServiceAccounts.Resolve(enteredAccount));
                account = selected.Name;
                ServiceAccount.Text = selected.Name;
                var profile = new WindowsServiceProfile(sid, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) { ServiceSid = selected.Sid };
                await profile.SaveAsync(DesktopPaths.DataDirectory);
                WindowsFtpCredentialStore.SetServiceCache(new(DesktopPaths.DataDirectory, sid, selected.Sid));
                var allJobs = (await store.LoadAsync()).Concat(jobs).ToArray();
                var allCopies = (await catalog.ReadAsync()).Concat(await EffectiveCatalog.ReadAsync()).DistinctBy(copy => copy.Id).ToArray();
                await WindowsFtpCredentialStore.ExportProfileForServiceAsync(allJobs, allCopies);
                await new WindowsSqlServerCredentialStore().ExportMonitorsForServiceAsync(
                    await new ExternalBackupMonitorStore(Path.Combine(DesktopPaths.DataDirectory, "external-monitors.json")).LoadAsync());
                await ExportServerMonitoringForServiceAsync();
                await new WindowsNotificationCredentialStore().ExportForServiceAsync(DesktopPaths.DataDirectory);
                await new WindowsRemoteGatewayConnections(DesktopPaths.DataDirectory).ExportForServiceAsync(jobs);
            }
            RefreshService();
            var pipeName = "OneCBackup-Service-" + Guid.NewGuid().ToString("N");
            var acl = new PipeSecurity(); acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 16384, 16384, acl);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            using var helper = Process.Start(new ProcessStartInfo(ServiceExecutable) { UseShellExecute = true, Verb = "runas",
                Arguments = "--manage-service " + pipeName }) ?? throw new InvalidOperationException("Не удалось запустить установку службы.");
            await pipe.WaitForConnectionAsync(deadline.Token);
            await ServiceManagementProtocol.WriteAsync(pipe, new ServiceManagementRequest(action, ServiceExecutable,
                DesktopPaths.DataDirectory, account, sid, action == "install" ? ServicePassword.Password : null), deadline.Token);
            ServicePassword.Clear();
            var result = await ServiceManagementProtocol.ReadAsync<ServiceManagementResult>(pipe, deadline.Token);
            if (!result.Success) throw new InvalidOperationException(result.Message);
            if (action == "install") { CurrentUserAgentStartup.RemoveOwnedEntry(); AgentStartup.IsChecked = CurrentUserAgentStartup.IsEnabled(); }
            Status.Text = result.Message;
        }
        catch (Exception error) { Status.Text = "Управление службой: " + error.Message; }
        finally { ServicePassword.Clear(); serviceManaging = false; ServiceAccount.IsEnabled = ServicePassword.IsEnabled = true; UpdateRunControls(); RefreshService(); }
    }
    private async void StartService_Click(object sender, RoutedEventArgs e)
    {
        if (!StartServiceButton.IsEnabled || ConfigurationWorkInProgress || !configurationReady) return;
        backgroundStarting = true; UpdateRunControls();
        try
        {
            await ValidateExecutionConnectionAsync();
            if (agentSettings?.Mode == AgentMode.Managed) await ExportServerMonitoringForServiceAsync();
            WindowsAgentService.Start(ServiceExecutable, DesktopPaths.DataDirectory); await RefreshBackgroundStateAsync();
        }
        catch (Exception error) { backgroundStarting = false; Status.Text = error.Message; UpdateRunControls(); }
        RefreshService();
    }
    private async void StopService_Click(object sender, RoutedEventArgs e)
    {
        try { await StopOwnedServiceAsync(); await RefreshBackgroundStateAsync(); }
        catch (Exception error) { Status.Text = error.Message; }
        RefreshService();
    }
    private async Task<bool> StopOwnedServiceAsync()
    {
        if (OwnService() is not { } info) return false;
        if (info.State != 1)
        {
            await Task.Run(() => WindowsAgentService.Stop(ServiceExecutable, DesktopPaths.DataDirectory));
            var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
            while (OwnService() is { State: not 1 })
            {
                if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Служба ещё завершает операцию. Дождитесь остановки и повторите действие.");
                await Task.Delay(200);
            }
        }
        return true;
    }
}
