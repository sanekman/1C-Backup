using System.IO;
using System.Net.Http;
using System.Windows;
using SPBackup.Core;
namespace SPBackup.Desktop;
public partial class MainWindow
{
    private ServerMonitoringSettings? displayedServerConnection;
    private bool checkingServerConnection;
    private bool serverMonitoringBusy;
    internal bool MonitoringSendFailedInWindow { get; private set; }

    private void ShowServerConnection(ServerMonitoringSettings? connection)
    {
        if (displayedServerConnection != connection)
        {
            MonitoringSendFailedInWindow = false;
            ServerConnectionLastSuccess.Text = connection is null ? "" : "Успешный ответ из окна ещё не получен.";
        }
        displayedServerConnection = connection;
        ServerAddress.Text = connection?.Address ?? "Сервер не подключён";
        ServerConnectionCheckStatus.Text = "";
        ServerMonitoringStatus.Text = connection is null ? "" : "Ожидается результат отправки состояния.";
        ConnectServerMonitoringButton.Content = connection is null ? "Подключить по приглашению…" : "Сменить подключение…";
        CheckServerConnectionButton.Visibility = DisconnectServerMonitoringButton.Visibility =
            connection is null ? Visibility.Collapsed : Visibility.Visible;
        RecoveryKitExportButton.Visibility = connection is null ? Visibility.Collapsed : Visibility.Visible;
        RecoveryKitExportButton.Content = connection?.AllowRecoveryKitExport == true
            ? "Запретить скачивание копий через сервер" : "Разрешить скачивание копий через сервер…";
        CheckServerConnectionButton.IsEnabled = connection is not null && !checkingServerConnection;
        DisconnectServerMonitoringButton.IsEnabled = connection is not null;
    }

    private async void RecoveryKitExport_Click(object sender, RoutedEventArgs e)
    {
        if (!ready || ConfigurationWorkInProgress || displayedServerConnection is not { } displayed) return;
        if (!displayed.AllowRecoveryKitExport && MessageBox.Show(this,
            "Фоновый агент сможет передавать этому серверу зарегистрированные файловые копии. Доступ к скачиванию назначает администратор сервера.",
            "Скачивание файловых копий", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        RecoveryKitExportButton.IsEnabled = false;
        changingAgentConfiguration = true;
        try
        {
            var current = await ServerMonitoringDispatcher.LoadSettingsAsync(DesktopPaths.DataDirectory, notificationCancellation.Token);
            if (displayed != current) throw new InvalidDataException("Подключение изменилось. Проверьте настройки сервера.");
            var updated = displayed with { AllowRecoveryKitExport = !displayed.AllowRecoveryKitExport };
            await ServerMonitoringDispatcher.SaveSettingsAsync(DesktopPaths.DataDirectory, updated, notificationCancellation.Token);
            ShowServerConnection(updated);
            ServerMonitoringStatus.Text = updated.AllowRecoveryKitExport
                ? "Скачивание разрешено для этого сервера. Комплекты подготавливает фоновый агент." : "Скачивание запрещено.";
        }
        catch (OperationCanceledException) when (notificationCancellation.IsCancellationRequested) { }
        catch (Exception error) { ServerMonitoringStatus.Text = error.Message; }
        finally
        {
            changingAgentConfiguration = false;
            RecoveryKitExportButton.IsEnabled = true;
            UpdateRunControls();
        }
    }

    private async void CheckServerConnection_Click(object sender, RoutedEventArgs e)
    {
        if (checkingServerConnection || displayedServerConnection is not { } connection) return;
        checkingServerConnection = true;
        CheckServerConnectionButton.IsEnabled = false;
        ServerConnectionCheckStatus.Text = "Проверяем ответ сервера из окна…";
        string result;
        try
        {
            using var client = new ServerMonitoringClient(connection);
            var available = await client.CheckReadinessAsync(notificationCancellation.Token);
            if (available && ReferenceEquals(displayedServerConnection, connection))
                ServerConnectionLastSuccess.Text = $"Последний успешный ответ из окна: {DateTime.Now:dd.MM HH:mm}.";
            result = available ? "Сервер ответил и готов." : "Сервер ответил, но готовность не подтверждена.";
        }
        catch (OperationCanceledException) when (notificationCancellation.IsCancellationRequested) { return; }
        catch (OperationCanceledException) { result = "Сервер не ответил вовремя."; }
        catch (HttpRequestException) { result = "Нет ответа по HTTPS. Проверьте сеть, адрес и сертификат сервера."; }
        catch (InvalidDataException) { result = "Проверьте адрес сервера и настройки сертификата."; }
        finally
        {
            checkingServerConnection = false;
            CheckServerConnectionButton.IsEnabled = displayedServerConnection is not null;
        }
        if (ReferenceEquals(displayedServerConnection, connection))
            ServerConnectionCheckStatus.Text = $"Проверка из окна в {DateTime.Now:dd.MM HH:mm}. {result}";
    }

    private DateTimeOffset serverMonitoringNext;
    private async Task PollServerMonitoringAsync()
    {
        if (!ready || SmokeMode || serverMonitoringBusy || backgroundBusy || DateTimeOffset.UtcNow < serverMonitoringNext) return;
        if (displayedServerConnection is not { } connection) return;
        serverMonitoringBusy=true;
        serverMonitoringNext=DateTimeOffset.UtcNow.AddSeconds(15);
        try
        {
            await new ServerMonitoringDispatcher(DesktopPaths.DataDirectory, ftpCredentials).PollAsync(notificationCancellation.Token);
            if (ReferenceEquals(displayedServerConnection, connection))
            {
                MonitoringSendFailedInWindow = false;
                ServerMonitoringStatus.Text = $"Последняя отправка из окна: {DateTime.Now:dd.MM HH:mm}.";
            }
        }
        catch (OperationCanceledException) when (notificationCancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (ReferenceEquals(displayedServerConnection, connection))
            {
                MonitoringSendFailedInWindow = true;
                ServerMonitoringStatus.Text = "Отправка из окна не удалась. История хранится локально.";
            }
        }
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
            ShowServerConnection(null);
            ServerMonitoringStatus.Text="Мониторинг отключён. Доступ можно отозвать в панели сервера.";
        }
        catch (Exception error) { ServerMonitoringStatus.Text=error.Message; }
    }
}
