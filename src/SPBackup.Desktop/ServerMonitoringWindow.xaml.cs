using System.Windows;
using SPBackup.Core;
namespace SPBackup.Desktop;
public partial class ServerMonitoringWindow : Window
{
    private readonly Guid agentId;
    private readonly WindowsFtpCredentialStore credentials;
    public ServerMonitoringWindow(Guid id, WindowsFtpCredentialStore store)
    { InitializeComponent(); agentId=id; credentials=store; Closed += (_,_)=>Invitation.Clear(); }
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        Connect.IsEnabled=false;
        try
        {
            var key="server-monitoring-"+Guid.NewGuid().ToString("N");
            var settings = new ServerMonitoringSettings(Address.Text.Trim().TrimEnd('/')+"/",agentId,key,Fingerprint.Text.Replace(":","").Replace(" ","").Trim().ToUpperInvariant());
            settings.Validate();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var client = new ServerMonitoringClient(settings);
            var secret = await client.EnrollAsync(agentId,Invitation.Password.Trim(),Environment.MachineName,timeout.Token);
            credentials.Save(key,"agent",secret);
            await ServerMonitoringDispatcher.SaveSettingsAsync(DesktopPaths.DataDirectory,settings,timeout.Token);
            await credentials.ExportForServiceAsync(key,timeout.Token);
            Invitation.Clear(); DialogResult=true;
        }
        catch (Exception error) { Status.Text="Подключение не выполнено: "+error.Message; }
        finally { Connect.IsEnabled=true; }
    }
}
