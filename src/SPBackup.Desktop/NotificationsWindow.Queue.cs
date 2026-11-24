using System.Windows;
using System.Windows.Controls;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class NotificationsWindow
{
    private readonly BackupNotificationQueue queue;
    private bool queueBusy;

    private sealed record DeliveryRow(NotificationQueueEntry Entry)
    {
        public string Channel => Entry.Provider switch { NotificationProvider.Telegram => "Telegram", NotificationProvider.Max => "MAX", _ => "Прежний канал" };
        public string Run => Entry.RunId.ToString("N")[..8];
        public string State => Entry.State switch
        {
            NotificationDeliveryState.Pending => "Ожидает отправки",
            NotificationDeliveryState.Sending => "Отправляется",
            NotificationDeliveryState.Retry => "Ожидает повтора",
            NotificationDeliveryState.Unknown => "Исход неизвестен",
            NotificationDeliveryState.Blocked => "Требуется действие",
            NotificationDeliveryState.Accepted => "Принято сервисом",
            NotificationDeliveryState.Expired => "Срок истёк",
            _ => "Отменено"
        };
        public string Timestamp => (Entry.AcceptedUtc ?? Entry.NextAttemptUtc ?? Entry.FinishedUtc).ToLocalTime().ToString("dd.MM HH:mm:ss");
    }

    private async void WorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, WorkspaceTabs) || !IsLoaded) return;
        Result.Text = "";
        Save.Visibility = WorkspaceTabs.SelectedItem == QueueTab ? Visibility.Collapsed : Visibility.Visible;
        if (WorkspaceTabs.SelectedItem == QueueTab) await RefreshQueueAsync();
    }

    private async void RefreshQueue_Click(object sender, RoutedEventArgs e) => await RefreshQueueAsync();

    private async Task RefreshQueueAsync()
    {
        if (queueBusy) return;
        queueBusy = true;
        UpdateQueueControls();
        try { await LoadQueueAsync(); }
        catch (Exception) { QueueCount.Text = "Не удалось прочитать очередь. Проверьте доступ к профилю и обновите список."; DeliveryGrid.ItemsSource = null; }
        finally { queueBusy = false; UpdateQueueControls(); }
    }

    private async Task LoadQueueAsync()
    {
        var previous = (DeliveryGrid.SelectedItem as DeliveryRow)?.Entry;
        var view = await queue.ViewAsync();
        var rows = view.Entries.Select(entry => new DeliveryRow(entry)).ToArray();
        DeliveryGrid.ItemsSource = rows;
        DeliveryGrid.SelectedItem = rows.FirstOrDefault(row => row.Entry.ChannelId == previous?.ChannelId && row.Entry.RunId == previous?.RunId);
        QueueCount.Text = rows.Length == 0 ? "Очередь пуста." : $"Незавершённых доставок: {view.Outstanding} · показано: {rows.Length}";
    }

    private void DeliveryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateQueueControls();

    private void UpdateQueueControls()
    {
        var selected = (DeliveryGrid.SelectedItem as DeliveryRow)?.Entry;
        RefreshQueue.IsEnabled = !queueBusy;
        RetryDelivery.IsEnabled = !queueBusy && selected?.CanRetry == true;
        CancelDelivery.IsEnabled = !queueBusy && selected?.CanCancel == true;
        QueueDetail.Text = selected?.Failure is { } failure ? new NotificationSendException(failure).Message : "";
    }

    private async void RetryDelivery_Click(object sender, RoutedEventArgs e) => await ChangeDeliveryAsync(retry: true);
    private async void CancelDelivery_Click(object sender, RoutedEventArgs e) => await ChangeDeliveryAsync(retry: false);

    private async Task ChangeDeliveryAsync(bool retry)
    {
        if (queueBusy || DeliveryGrid.SelectedItem is not DeliveryRow row) return;
        if ((!retry || row.Entry.State == NotificationDeliveryState.Unknown)
            && MessageBox.Show(this, retry ? "Сообщение могло быть принято. Повторить доставку с риском дубля?"
                : "Отменить выбранную доставку?", "Оповещения", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        queueBusy = true;
        UpdateQueueControls();
        try
        {
            if (retry) await queue.RetryAsync(row.Entry.ChannelId, row.Entry.RunId);
            else await queue.CancelAsync(row.Entry.ChannelId, row.Entry.RunId);
            await LoadQueueAsync();
            Result.Text = retry ? "Выбранная доставка поставлена на повтор." : "Выбранная доставка отменена.";
        }
        catch (InvalidOperationException error) { Result.Text = error.Message; }
        catch (Exception) { Result.Text = "Не удалось изменить доставку. Проверьте доступ к очереди и обновите список."; }
        finally { queueBusy = false; UpdateQueueControls(); }
    }
}
