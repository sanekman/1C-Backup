using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SPBackup.Core;

namespace SPBackup.Desktop;

internal static partial class DesktopSmoke
{
    private sealed class NotificationFixtureSender : IBackupNotificationSender
    {
        public Task<NotificationAcceptance> SendAsync(NotificationChannel channel, string text, CancellationToken token = default) =>
            channel.Provider == NotificationProvider.Telegram
                ? Task.FromException<NotificationAcceptance>(new NotificationSendException(NotificationFailure.Authorization))
                : Task.FromResult(new NotificationAcceptance("fixture"));
    }

    private static async Task VerifyNotificationWindowAsync(MainWindow owner, string output)
    {
        Directory.CreateDirectory(output);
        var lab = Path.Combine(DesktopPaths.DataDirectory, "notifications-smoke");
        var now = DateTimeOffset.UtcNow;
        var telegram = new NotificationChannel
            { Provider = NotificationProvider.Telegram, Enabled = true, Recipient = "-100123", CredentialKey = "fixture", EnabledSinceUtc = now.AddHours(-1) };
        var max = telegram with { Id = Guid.NewGuid(), Provider = NotificationProvider.Max };
        await new NotificationSettingsStore(lab).SaveAsync(new() { Channels = [telegram, max] });
        var run = new BackupRun(Guid.NewGuid(), Guid.NewGuid(), "Тестовое задание", now.AddMinutes(-1), now, BackupRunStatus.Failed, "fixture");
        await new BackupRunHistory(Path.Combine(lab, "runs.json")).SaveAsync(run);
        var queue = new BackupNotificationQueue(lab);
        await new BackupNotificationDispatcher(lab, new NotificationFixtureSender()).PollAsync();

        var original = DesktopTheme.Preference;
        var dialog = new NotificationsWindow(lab) { Owner = owner, Width = 900, Height = 580 };
        T Control<T>(string name) where T : FrameworkElement => (T)dialog.FindName(name);
        try
        {
            dialog.Show();
            var tabs = Control<TabControl>("WorkspaceTabs");
            var grid = Control<DataGrid>("DeliveryGrid");
            var retry = Control<Button>("RetryDelivery");
            var cancel = Control<Button>("CancelDelivery");
            await WaitUntil(() => Control<CheckBox>("TelegramEnabled").IsChecked == true, "Настройки Telegram не загрузились.");
            tabs.SelectedItem = Control<TabItem>("QueueTab");
            await WaitUntil(() => grid.Items.Count == 2 && Control<Button>("RefreshQueue").IsEnabled, "Очередь не отобразила оба канала.");

            foreach (var theme in new[] { DesktopThemePreference.Light, DesktopThemePreference.Dark })
            {
                DesktopTheme.Select(theme);
                dialog.UpdateLayout();
                var settingsButton = Control<Button>("Save");
                var actionBottom = cancel.TranslatePoint(new Point(0, cancel.ActualHeight), dialog).Y;
                if (dialog.ActualWidth != 900 || dialog.ActualHeight != 580 || grid.ActualHeight < 200
                    || !cancel.IsVisible || actionBottom >= dialog.ActualHeight || settingsButton.IsVisible)
                    throw new InvalidOperationException("Очередь и действия не помещаются в окно 900×580.");
                grid.SelectedIndex = 0;
                if (!retry.IsEnabled || !cancel.IsEnabled || !Control<TextBlock>("QueueDetail").Text.Contains("авторизацию"))
                    throw new InvalidOperationException("Заблокированная доставка потеряла доступные действия или причину.");
                var image = new RenderTargetBitmap(900, 580, 96, 96, PixelFormats.Pbgra32);
                image.Render(dialog);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var file = File.Create(Path.Combine(output, $"notifications-{theme.ToString().ToLowerInvariant()}.png"));
                encoder.Save(file);
                grid.SelectedIndex = 1;
                if (retry.IsEnabled || cancel.IsEnabled) throw new InvalidOperationException("Для принятого сообщения доступны изменения.");
            }

            grid.SelectedIndex = 0;
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => Control<TextBlock>("Result").Text.Contains("поставлена на повтор"), "Повтор не выполнен через кнопку.");
            if (!(await queue.ViewAsync()).Entries.Any(entry => entry.Provider == NotificationProvider.Telegram
                && entry.State == NotificationDeliveryState.Pending))
                throw new InvalidOperationException("Повтор не сохранился в очереди.");
            await queue.CancelAsync(telegram.Id, run.RunId);
            Control<Button>("RefreshQueue").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => Control<TextBlock>("QueueCount").Text.Contains("доставок: 0"), "Отмена не отобразилась в очереди.");
        }
        finally
        {
            dialog.Close();
            DesktopTheme.Select(original);
        }
    }
}
