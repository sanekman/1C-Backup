using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class NotificationsWindow : Window
{
    private readonly string directory;
    private readonly NotificationSettingsStore store;
    private readonly WindowsNotificationCredentialStore secrets = new();
    private NotificationSettings settings = new();
    private bool busy, loaded;
    public NotificationsWindow() : this(DesktopPaths.DataDirectory) { }

    internal NotificationsWindow(string directory)
    {
        this.directory = directory;
        store = new(directory);
        queue = new(directory);
        InitializeComponent();
        Closing += (_, e) => e.Cancel = busy;
        Loaded += async (_, _) =>
        {
            try
            {
                settings = await store.LoadAsync();
                Errors.IsChecked = settings.Errors; Warnings.IsChecked = settings.Warnings;
                Success.IsChecked = settings.Success; Cancelled.IsChecked = settings.Cancelled;
                var telegram = Channel(NotificationProvider.Telegram); var max = Channel(NotificationProvider.Max);
                TelegramEnabled.IsChecked = telegram.Enabled; TelegramRecipient.Text = telegram.Recipient;
                MaxEnabled.IsChecked = max.Enabled; MaxRecipient.Text = max.Recipient; MaxTarget.SelectedIndex = max.MaxUser ? 1 : 0;
                loaded = true;
            }
            catch (Exception error) { Result.Text = "Не удалось прочитать настройки оповещений. Сохранение отключено."; Save.IsEnabled = false; DiagnosticLog.Current.Write(DiagnosticEvent.NotificationFailed, DiagnosticLevel.Warning, error); }
        };
    }
    private NotificationChannel Channel(NotificationProvider provider) => settings.Channels.FirstOrDefault(c => c.Provider == provider) ?? new() { Provider = provider };
    private NotificationChannel Read(NotificationProvider provider, bool test = false)
    {
        var old = Channel(provider); var telegram = provider == NotificationProvider.Telegram;
        var recipient = (telegram ? TelegramRecipient.Text : MaxRecipient.Text).Trim();
        var token = telegram ? TelegramToken.Password : MaxToken.Password;
        var enabled = test || (telegram ? TelegramEnabled.IsChecked : MaxEnabled.IsChecked) == true;
        var maxUser = !telegram && MaxTarget.SelectedIndex == 1;
        var newBot = token.Length > 0 && (telegram ? TelegramNewBot.IsChecked : MaxNewBot.IsChecked) == true;
        var changed = recipient != old.Recipient || maxUser != old.MaxUser || newBot || enabled && !old.Enabled;
        return old with { Enabled = enabled, Recipient = recipient, MaxUser = maxUser,
            Id = changed ? Guid.NewGuid() : old.Id, EnabledSinceUtc = changed ? DateTimeOffset.UtcNow : old.EnabledSinceUtc,
            CredentialKey = token.Length > 0 ? "bot-" + Guid.NewGuid().ToString("N") : old.CredentialKey };
    }
    private void SaveToken(NotificationChannel channel)
    {
        var box = channel.Provider == NotificationProvider.Telegram ? TelegramToken : MaxToken;
        if (box.Password.Length > 0) { BotNotificationSender.ValidateToken(channel.Provider, box.Password); secrets.Save(channel.CredentialKey, box.Password); }
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !loaded) return; busy = true; Save.IsEnabled = false;
        try
        {
            if (TelegramNewBot.IsChecked == true && TelegramToken.Password.Length == 0 ||
                MaxNewBot.IsChecked == true && MaxToken.Password.Length == 0)
                throw new InvalidDataException("Введите токен другого бота или снимите отметку о замене.");
            var updated = new NotificationSettings { Errors = Errors.IsChecked == true, Warnings = Warnings.IsChecked == true,
                Success = Success.IsChecked == true, Cancelled = Cancelled.IsChecked == true,
                Channels = [Read(NotificationProvider.Telegram), Read(NotificationProvider.Max)] };
            if (updated.Errors != settings.Errors || updated.Warnings != settings.Warnings || updated.Success != settings.Success || updated.Cancelled != settings.Cancelled)
                updated = updated with { Channels = updated.Channels.Select(c => c with { Id = Guid.NewGuid(), EnabledSinceUtc = DateTimeOffset.UtcNow }).ToArray() };
            updated.Validate(); foreach (var channel in updated.Channels) SaveToken(channel);
            await store.SaveAsync(updated); settings = updated; TelegramToken.Clear(); MaxToken.Clear();
            TelegramNewBot.IsChecked = MaxNewBot.IsChecked = false;
            Result.Text = "Сохранено. Программа и агент применят настройки автоматически.";
        }
        catch (Exception error) { Result.Text = error is InvalidDataException ? error.Message : "Не удалось сохранить настройки или токен. Проверьте доступ к профилю Windows."; DiagnosticLog.Current.Write(DiagnosticEvent.NotificationFailed, DiagnosticLevel.Warning, error); }
        finally { busy = false; Save.IsEnabled = loaded; }
    }
    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !loaded) return; busy = true; var button = (Button)sender; button.IsEnabled = false;
        try
        {
            var provider = (string)button.Tag == "Telegram" ? NotificationProvider.Telegram : NotificationProvider.Max;
            var channel = Read(provider, true); new NotificationSettings { Channels = [channel] }.Validate();
            // Unsaved tokens are kept in memory for the test, not written into a configuration file.
            var text = provider == NotificationProvider.Telegram ? TelegramToken.Password : MaxToken.Password;
            IFtpCredentialStore credentials = text.Length > 0 ? new TestCredential(text) : secrets;
            using var client = BotNotificationSender.CreateClient();
            await new BotNotificationSender(client, credentials).SendAsync(channel, "SP Backup: тестовое оповещение. Подключение настроено.");
            Result.Text = "Сервис принял тестовое сообщение. Проверьте его в чате и сохраните настройки.";
        }
        catch (Exception error)
        {
            Result.Text = error is NotificationSendException or InvalidDataException ? error.Message
                : "Не удалось выполнить тест. Проверьте настройки оповещений и доступ к профилю Windows.";
            DiagnosticLog.Current.Write(DiagnosticEvent.NotificationFailed, DiagnosticLevel.Warning, error as NotificationSendException);
        }
        finally { busy = false; button.IsEnabled = true; }
    }
    private async void TestInAgent_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !loaded) return;
        var button = (Button)sender;
        var provider = (string)button.Tag == "Telegram" ? NotificationProvider.Telegram : NotificationProvider.Max;
        var saved = Channel(provider);
        var draft = Read(provider);
        if (!saved.Enabled || draft != saved)
        {
            Result.Text = "Включите канал и сохраните изменения перед тестом через агент.";
            return;
        }
        busy = true;
        button.IsEnabled = false;
        Result.Text = "Ожидание ответа фонового агента…";
        try
        {
            var result = await new LocalNotificationTests(directory).TestAsync(provider);
            Result.Text = result.Outcome switch
            {
                LocalNotificationTestOutcome.Accepted => $"Сервис принял тест от фонового агента (PID {result.ProcessId}). Проверьте чат.",
                LocalNotificationTestOutcome.ChannelChanged => "Канал изменился до теста. Сохраните настройки и повторите.",
                _ => new NotificationSendException(result.Failure ?? NotificationFailure.OutcomeUnknown).Message
            };
        }
        catch (InvalidOperationException error) { Result.Text = error.Message; }
        catch (Exception) { Result.Text = "Не удалось получить результат фонового агента. Проверьте доступ к профилю."; }
        finally { busy = false; button.IsEnabled = true; }
    }
    private sealed class TestCredential(string value) : IFtpCredentialStore
    { public Task<System.Net.NetworkCredential> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(new System.Net.NetworkCredential("bot", value)); }
    private void Help_Click(object sender, RoutedEventArgs e)
    {
        var uri = (string)((Button)sender).Tag == "Telegram" ? "https://core.telegram.org/bots/tutorial" : "https://dev.max.ru/docs-api/use-cases/getting-chat-id";
        var local = Path.Combine(AppContext.BaseDirectory, "Help", "notifications.txt");
        try { Process.Start(new ProcessStartInfo(File.Exists(local) ? local : uri) { UseShellExecute = true }); }
        catch { Result.Text = "Инструкция: " + uri; }
    }
}
