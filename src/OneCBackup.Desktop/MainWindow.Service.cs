using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

public partial class MainWindow
{
    private bool serviceManaging;
    private string ServiceExecutable => Path.Combine(AppContext.BaseDirectory, "OneCBackup.Agent.exe");
    private WindowsAgentServiceInfo? OwnService()
    {
        var info = WindowsAgentService.QueryInfo();
        return info?.Matches(ServiceExecutable, DesktopPaths.DataDirectory) == true ? info : null;
    }
    private async Task InitializeServiceAsync()
    {
        if (SmokeMode) { RefreshService(); return; }
        ServiceAccount.Text = WindowsIdentity.GetCurrent().Name;
        if (await WindowsServiceProfile.LoadAsync(DesktopPaths.DataDirectory) is { } profile)
        {
            profile.AssertCurrentUser();
            WindowsFtpCredentialStore.SetServiceCache(new(DesktopPaths.DataDirectory, profile.OwnerSid));
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
            ServiceStatus.Text = info is null ? "Служба не установлена." : owned ? "Служба: " + info.Description
                : "Служба установлена для другой папки программы или профиля.";
            InstallServiceButton.IsEnabled = ready && !serviceManaging && info is null && ApplicationUpdateHandoff.IsInstalled();
            RemoveServiceButton.IsEnabled = ready && !serviceManaging && owned && info!.State == 1;
            StartServiceButton.IsEnabled = ready && !serviceManaging && owned && info!.State == 1;
            StopServiceButton.IsEnabled = ready && !serviceManaging && owned && info!.State == 4;
        }
        catch (Exception error) { ServiceStatus.Text = error.Message; }
    }
    private async void InstallService_Click(object sender, RoutedEventArgs e) => await ManageServiceAsync("install");
    private async void RemoveService_Click(object sender, RoutedEventArgs e) => await ManageServiceAsync("remove");
    private async Task ManageServiceAsync(string action)
    {
        if (!ready || serviceManaging || SmokeMode) return;
        serviceManaging = true; UpdateRunControls();
        try
        {
            if (!ApplicationUpdateHandoff.IsInstalled()) throw new InvalidOperationException("Для службы установите программу установщиком Windows.");
            if (backgroundBusy || activeRun is not null || schedulingTask is not null || manualPending || persisting)
                throw new InvalidOperationException("Сначала остановите автоматическое копирование и дождитесь окончания операций.");
            if (agentSettings?.Mode != AgentMode.Standalone) throw new InvalidOperationException("Сначала сохраните автономный режим агента.");
            var identity = WindowsIdentity.GetCurrent(); var sid = identity.User!.Value;
            if (action == "install")
            {
                if (string.IsNullOrEmpty(ServicePassword.Password)) throw new InvalidOperationException("Введите пароль Windows, а не PIN.");
                var profile = new WindowsServiceProfile(sid, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
                await profile.SaveAsync(DesktopPaths.DataDirectory);
                WindowsFtpCredentialStore.SetServiceCache(new(DesktopPaths.DataDirectory, sid));
                await WindowsFtpCredentialStore.ExportProfileForServiceAsync(jobs, await catalog.ReadAsync());
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
                DesktopPaths.DataDirectory, identity.Name, sid, action == "install" ? ServicePassword.Password : null), deadline.Token);
            ServicePassword.Clear();
            var result = await ServiceManagementProtocol.ReadAsync<ServiceManagementResult>(pipe, deadline.Token);
            if (!result.Success) throw new InvalidOperationException(result.Message);
            if (action == "install") { CurrentUserAgentStartup.RemoveOwnedEntry(); AgentStartup.IsChecked = CurrentUserAgentStartup.IsEnabled(); }
            Status.Text = result.Message;
        }
        catch (Exception error) { Status.Text = "Управление службой: " + error.Message; }
        finally { ServicePassword.Clear(); serviceManaging = false; UpdateRunControls(); RefreshService(); }
    }
    private async void StartService_Click(object sender, RoutedEventArgs e)
    {
        try { WindowsAgentService.Start(ServiceExecutable, DesktopPaths.DataDirectory); await RefreshBackgroundStateAsync(); }
        catch (Exception error) { Status.Text = error.Message; }
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
