using System.IO;
using System.Windows;
using Microsoft.Win32;
using OneCBackup.Core;
using OneCBackup.Remote;

namespace OneCBackup.Desktop;

public partial class MainWindow
{
    private RemoteGatewaySettings? remoteGatewaySettings;
    private readonly CancellationTokenSource hubShutdown = new();
    private Task? hubTask;
    private void StorageBridgeHelp_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Help", "storage-bridge.txt")) { UseShellExecute = true }); }
        catch { Status.Text = "Инструкция находится в Help/storage-bridge.txt в папке программы."; }
    }
    private async Task InitializeRemoteHubAsync()
    {
        try
        {
            var path = Path.Combine(DesktopPaths.DataDirectory, "remote-hub.json");
            if (File.Exists(path))
            { var options = await RemoteConfiguration.ReadAsync<HubOptions>(path); options.Validate(); HubStatus.Text = options.Enabled ? "Узел включён в настройках · локальный порт " + options.Port + ". Публикация HTTPS выполняется через Caddy / IIS / Apache." : "Узел выключен."; }
        }
        catch { HubStatus.Text = "Не удалось прочитать настройки публичного узла."; }
    }
    private void UpdateRemoteGatewayStatus()
    {
        if (RemoteGatewayStatus is not null) RemoteGatewayStatus.Text = remoteGatewaySettings is null ? "Шлюз не подключён. Импортируйте client-pairing.json из настройки контейнера."
            : "Шлюз " + remoteGatewaySettings.GatewayId.ToString("N")[..8] + " · " + remoteGatewaySettings.Endpoint;
    }
    private async void RemoteGatewayImport_Click(object sender, RoutedEventArgs e)
    {
        if (!editorOpen || persisting) return;
        var file = new OpenFileDialog { Filter = "Подключение шлюза (*.json)|*.json", Title = "Подключить шлюз хранилища" };
        if (file.ShowDialog(this) != true) return;
        try
        {
            remoteGatewaySettings = await new WindowsRemoteGatewayConnections(DesktopPaths.DataDirectory).ImportAsync(file.FileName);
            Destination.Text = "remote"; EncryptArchive.IsChecked = true; ArchiveCopies.IsChecked = true;
            UpdateRemoteGatewayStatus(); EditorFieldChanged(sender, e);
            Status.Text = "Шлюз подключён. Задайте пароль архива. Файл client-pairing.json содержит секреты — храните его защищённо.";
        }
        catch (Exception error) { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, error); Status.Text = "Не удалось импортировать подключение шлюза. Проверьте файл и доступ к защищённому хранилищу Windows."; }
    }
    private async void HubImport_Click(object sender, RoutedEventArgs e)
    {
        var file = new OpenFileDialog { Filter = "Настройки публичного узла (*.json)|*.json", Title = "Импортировать hub.json" };
        if (file.ShowDialog(this) != true) return;
        try
        {
            var options = await RemoteConfiguration.ReadAsync<HubOptions>(file.FileName); options.Validate();
            if (!options.Enabled) throw new InvalidDataException();
            var target = Path.Combine(DesktopPaths.DataDirectory, "remote-hub.json");
            if (File.Exists(target))
            {
                var existing = await RemoteConfiguration.ReadAsync<HubOptions>(target); existing.Validate();
                options = options with { Peers = existing.Peers.Concat(options.Peers).GroupBy(peer => peer.GatewayId).Select(group => group.Last()).ToArray() };
                options.Validate();
            }
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllTextAsync(temporary, System.Text.Json.JsonSerializer.Serialize(options, RemoteConfiguration.Json)); File.Move(temporary, target, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            HubStatus.Text = "Узел включён на локальном порту " + options.Port + ". Опубликуйте его через Caddy, IIS или Apache с HTTPS и WebSocket.";
        }
        catch (Exception error) { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, error); HubStatus.Text = "Не удалось импортировать настройки публичного узла."; }
    }
    private async void HubDisable_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(DesktopPaths.DataDirectory, "remote-hub.json");
            var options = File.Exists(path) ? await RemoteConfiguration.ReadAsync<HubOptions>(path) : new HubOptions();
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllTextAsync(temporary, System.Text.Json.JsonSerializer.Serialize(options with { Enabled = false }, RemoteConfiguration.Json)); File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            HubStatus.Text = "Публичный узел выключается. Соединения шлюзов будут закрыты.";
        }
        catch { HubStatus.Text = "Не удалось выключить узел. Проверьте доступ к профилю."; }
    }
}
