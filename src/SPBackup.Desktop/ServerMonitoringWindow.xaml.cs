using System.Windows;
using SPBackup.Core;
namespace SPBackup.Desktop;
public partial class ServerMonitoringWindow : Window
{
    private bool connecting;
    private readonly Guid agentId;
    private readonly WindowsFtpCredentialStore credentials;
    public ServerMonitoringWindow(Guid id, WindowsFtpCredentialStore store)
    { InitializeComponent(); agentId=id; credentials=store; Closed += (_,_)=>Invitation.Clear(); Closing += (_, e) => e.Cancel = connecting; }
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (connecting) return;
        if (string.IsNullOrWhiteSpace(Invitation.Password))
        {
            Status.Text = "Введите код приглашения из панели сервера.";
            Invitation.Focus();
            return;
        }
        connecting = true;
        Connect.IsEnabled = CancelConnection.IsEnabled = false;
        Status.Text = "Подключение к серверу…";
        try
        {
            var key="server-monitoring-"+Guid.NewGuid().ToString("N");
            var settings = new ServerMonitoringSettings(Address.Text.Trim().TrimEnd('/')+"/",agentId,key,Fingerprint.Text.Replace(":","").Replace(" ","").Trim().ToUpperInvariant());
            settings.Validate();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var client = new ServerMonitoringClient(settings);
            var secret = await client.EnrollAsync(agentId,Invitation.Password.Trim(),Environment.MachineName,timeout.Token);
            credentials.Save(key,"agent",secret);
            await credentials.ExportForServiceAsync(key,timeout.Token);
            await ServerMonitoringDispatcher.SaveSettingsAsync(DesktopPaths.DataDirectory,settings,timeout.Token);
            Invitation.Clear(); connecting = false; DialogResult = true;
        }
        catch (Exception error) { Status.Text="Подключение не выполнено: "+error.Message; }
        finally { connecting = false; Connect.IsEnabled = CancelConnection.IsEnabled = true; }
    }
}
