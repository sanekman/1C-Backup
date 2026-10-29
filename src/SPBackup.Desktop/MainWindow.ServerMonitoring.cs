using System.IO;
using System.Windows;
using SPBackup.Core;
namespace SPBackup.Desktop;
public partial class MainWindow
{
    private bool serverMonitoringBusy;
    private DateTimeOffset serverMonitoringNext;
    private async Task PollServerMonitoringAsync()
    {
        if (!ready || SmokeMode || serverMonitoringBusy || backgroundBusy || DateTimeOffset.UtcNow < serverMonitoringNext) return;
        serverMonitoringBusy=true;
        serverMonitoringNext=DateTimeOffset.UtcNow.AddSeconds(15);
        try
        {
            await new ServerMonitoringDispatcher(DesktopPaths.DataDirectory,ftpCredentials).PollAsync(notificationCancellation.Token);
            ServerMonitoringStatus.Text=File.Exists(Path.Combine(DesktopPaths.DataDirectory,"server-monitoring.json")) ? "Мониторинг подключён. Состояние передано серверу." : "Мониторинг не подключён.";
        }
        catch (OperationCanceledException) when (notificationCancellation.IsCancellationRequested) { }
        catch (Exception) { ServerMonitoringStatus.Text="Сервер мониторинга недоступен. История сохранена локально; отправка будет повторена."; }
        finally { serverMonitoringBusy=false; }
    }
    private async Task ExportServerMonitoringForServiceAsync()
    {
        var path=Path.Combine(DesktopPaths.DataDirectory,"server-monitoring.json");
        if (!File.Exists(path)) return;
        var settings=System.Text.Json.JsonSerializer.Deserialize<ServerMonitoringSettings>(await File.ReadAllTextAsync(path))
            ?? throw new InvalidDataException("Не прочитаны настройки мониторинга.");
        await ftpCredentials.ExportForServiceAsync(settings.CredentialKey);
    }
    private async void ConnectServerMonitoring_Click(object sender,RoutedEventArgs e)
    {
        if (!ready || agentSettings is null) return;
        if (ConfigurationWorkInProgress || agentSettings.Mode == AgentMode.Managed && !RequireConfigurationIdle())
        { Status.Text = "Дождитесь окончания операций и остановите серверный агент перед сменой подключения."; return; }
        using var lease = agentSettings.Mode == AgentMode.Managed ? AcquireEngineOrReport() : null;
        if (agentSettings.Mode == AgentMode.Managed && lease is null) return;
        changingAgentConfiguration = true; UpdateRunControls();
        try
        {
            if (new ServerMonitoringWindow(agentSettings.AgentId,ftpCredentials) { Owner=this }.ShowDialog()!=true) return;
            if (agentSettings.Mode == AgentMode.Managed)
            {
                configurationReady = false;
                var connection = await ServerMonitoringDispatcher.LoadSettingsAsync(DesktopPaths.DataDirectory);
                var updated = AgentModeTransition.Prepare(agentSettings, AgentMode.Managed, connection);
                await agentSettingsStore.SaveAsync(updated); agentSettings = updated;
                await LoadEffectiveJobsAsync(); await RefreshHistoryAsync();
            }
            await RefreshServerConnectionLabelAsync();
            serverMonitoringNext=DateTimeOffset.MinValue;
            ServerMonitoringStatus.Text="Сервер подключён. Выберите, откуда получать задания, и нажмите «Применить режим».";
        }
        catch (Exception error) { Status.Text = "Подключение требует внимания: " + error.Message + " Нажмите «Применить режим» после исправления."; }
        finally { changingAgentConfiguration = false; UpdateRunControls(); }
    }
    private void DisconnectServerMonitoring_Click(object sender,RoutedEventArgs e)
    {
        if (ConfigurationWorkInProgress) { Status.Text = "Сначала дождитесь окончания операций."; return; }
        if (agentSettings?.Mode != AgentMode.Standalone)
        { Status.Text = "Сначала выберите «Задания на этом компьютере» и нажмите «Применить режим». Затем отключите сервер."; return; }
        try
        {
            File.Delete(Path.Combine(DesktopPaths.DataDirectory,"server-monitoring.json"));
            ServerAddress.Text = "Подключение не настроено";
            ServerMonitoringStatus.Text="Мониторинг отключён. Доступ можно отозвать в панели сервера.";
        }
        catch (Exception error) { ServerMonitoringStatus.Text=error.Message; }
    }
}
