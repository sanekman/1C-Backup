using System.Windows;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private readonly System.Net.Http.HttpClient notificationClient = BotNotificationSender.CreateClient();
    private readonly CancellationTokenSource notificationCancellation = new();
    private BackupNotificationDispatcher? notificationDispatcher;
    private bool notificationBusy;
    private void Notifications_Click(object sender, RoutedEventArgs e) => new NotificationsWindow { Owner = this }.ShowDialog();
    private async Task PollNotificationsAsync()
    {
        if (notificationBusy || !ready || notificationCancellation.IsCancellationRequested || SmokeMode) return;
        notificationBusy = true;
        try
        {
            notificationDispatcher ??= new(DesktopPaths.DataDirectory, new BotNotificationSender(notificationClient, new WindowsNotificationCredentialStore()));
            var warning = await notificationDispatcher.PollAsync(notificationCancellation.Token);
            NotificationsStatus.Text = warning ?? "";
        }
        catch (OperationCanceledException) when (notificationCancellation.IsCancellationRequested) { }
        finally { notificationBusy = false; }
    }
}
