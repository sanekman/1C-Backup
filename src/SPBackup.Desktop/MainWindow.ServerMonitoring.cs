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
    private void ConnectServerMonitoring_Click(object sender,RoutedEventArgs e)
    {
        if (!ready || agentSettings is null) return;
        using var lease = agentSettings.Mode == AgentMode.Managed ? AcquireEngineOrReport() : null;
        if (agentSettings.Mode == AgentMode.Managed && lease is null) return;
        if (new ServerMonitoringWindow(agentSettings.AgentId,ftpCredentials) { Owner=this }.ShowDialog()==true)
        { serverMonitoringNext=DateTimeOffset.MinValue; ServerMonitoringStatus.Text="Подключено. Фоновый агент отправляет состояние каждые 15 секунд."; }
    }
    private void DisconnectServerMonitoring_Click(object sender,RoutedEventArgs e)
    {
        using var lease = agentSettings?.Mode == AgentMode.Managed ? AcquireEngineOrReport() : null;
        if (agentSettings?.Mode == AgentMode.Managed && lease is null) return;
        try
        {
            File.Delete(Path.Combine(DesktopPaths.DataDirectory,"server-monitoring.json"));
            ServerMonitoringStatus.Text="Мониторинг отключён. Доступ можно отозвать в панели сервера.";
        }
        catch (Exception error) { ServerMonitoringStatus.Text=error.Message; }
    }
}
