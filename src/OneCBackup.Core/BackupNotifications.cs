using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OneCBackup.Core;

public enum NotificationProvider { Telegram, Max }
public sealed record NotificationChannel
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public NotificationProvider Provider { get; init; }
    public bool Enabled { get; init; }
    public string Recipient { get; init; } = "";
    public bool MaxUser { get; init; }
    public string CredentialKey { get; init; } = "";
    public DateTimeOffset EnabledSinceUtc { get; init; }
}
public sealed record NotificationSettings
{
    public bool Success { get; init; }
    public bool Warnings { get; init; } = true;
    public bool Errors { get; init; } = true;
    public bool Cancelled { get; init; }
    public IReadOnlyList<NotificationChannel> Channels { get; init; } = [];
    public void Validate()
    {
        if (Channels is null || Channels.Count > 2 || Channels.Any(c => c is null || c.Id == Guid.Empty || !Enum.IsDefined(c.Provider))
            || Channels.Select(c => c.Provider).Distinct().Count() != Channels.Count || Channels.Select(c => c.Id).Distinct().Count() != Channels.Count)
            throw new InvalidDataException("Некорректные каналы оповещений.");
        foreach (var c in Channels.Where(c => c.Enabled))
            if (c.EnabledSinceUtc == default || string.IsNullOrWhiteSpace(c.CredentialKey) || c.CredentialKey.Length > 512
                || c.CredentialKey.Any(char.IsControl) || !ValidRecipient(c))
                throw new InvalidDataException("Укажите токен бота и корректный ID получателя для " + c.Provider + ".");
    }
    public static bool ValidRecipient(NotificationChannel c) => Regex.IsMatch(c.Recipient ?? "", @"^-?[1-9][0-9]{0,18}$")
        && long.TryParse(c.Recipient, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id)
        && (c.Provider != NotificationProvider.Max || !c.MaxUser || id > 0);
    public bool Includes(BackupRun run) => run.Status switch
    {
        BackupRunStatus.Succeeded => run.Warnings?.Count > 0 ? Warnings : Success,
        BackupRunStatus.Cancelled => Cancelled,
        BackupRunStatus.Failed or BackupRunStatus.PartiallySucceeded or BackupRunStatus.Interrupted => Errors,
        _ => false
    };
}
public sealed class NotificationSettingsStore(string directory)
{
    private readonly AgentJsonFile<NotificationSettings> file = new(Path.Combine(directory, "notifications.json"));
    public async Task<NotificationSettings> LoadAsync(CancellationToken token = default)
    { var value = await file.ReadAsync(token) ?? new(); value.Validate(); return value; }
    public Task SaveAsync(NotificationSettings settings, CancellationToken token = default)
    { settings.Validate(); return file.WriteAsync(settings, token); }
}
public interface IBackupNotificationSender
{
    Task SendAsync(NotificationChannel channel, string text, CancellationToken token = default);
}
/// <summary>Fixed HTTPS hosts, no redirects and no untrusted API error text in diagnostics or the UI.</summary>
public sealed class BotNotificationSender(HttpClient client, IFtpCredentialStore credentials) : IBackupNotificationSender
{
    public static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    public static void ValidateToken(NotificationProvider provider, string secret)
    {
        if (secret.Length is 0 or > 1280 || secret.Any(c => c > 126 || c < 33)
            || provider == NotificationProvider.Telegram && !Regex.IsMatch(secret, @"^[0-9]+:[A-Za-z0-9_-]+$"))
            throw new InvalidDataException("Некорректный токен бота.");
    }
    public async Task SendAsync(NotificationChannel channel, string text, CancellationToken token = default)
    {
        if (!NotificationSettings.ValidRecipient(channel) || !Enum.IsDefined(channel.Provider)) throw new InvalidDataException("Некорректный получатель оповещений.");
        if (text.Length is 0 or > 3900) throw new ArgumentException("Сообщение должно содержать до 3900 символов.");
        var secret = (await credentials.GetAsync(channel.CredentialKey, token)).Password;
        ValidateToken(channel.Provider, secret);
        var uri = channel.Provider == NotificationProvider.Telegram
            ? "https://api.telegram.org/bot" + secret + "/sendMessage"
            : "https://platform-api2.max.ru/messages?" + (channel.MaxUser ? "user_id=" : "chat_id=") + channel.Recipient;
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Content = channel.Provider == NotificationProvider.Telegram
            ? JsonContent.Create(new { chat_id = channel.Recipient, text }) : JsonContent.Create(new { text });
        if (channel.Provider == NotificationProvider.Max) request.Headers.TryAddWithoutValidation("Authorization", secret);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("Сервис оповещений вернул HTTP " + (int)response.StatusCode + ".", null, response.StatusCode);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var body = new MemoryStream(); var buffer = new byte[4096]; int count;
            while ((count = await stream.ReadAsync(buffer, deadline.Token)) > 0)
            { if (body.Length + count > 65536) throw new InvalidDataException("Ответ сервиса оповещений слишком большой."); body.Write(buffer, 0, count); }
            using var json = JsonDocument.Parse(body.ToArray());
            var root = json.RootElement;
            var accepted = channel.Provider == NotificationProvider.Telegram
                ? root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
                    && root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
                    && result.TryGetProperty("message_id", out var messageId) && messageId.TryGetInt64(out _)
                : root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                    && message.TryGetProperty("body", out var messageBody) && messageBody.ValueKind == JsonValueKind.Object
                    && messageBody.TryGetProperty("mid", out var mid) && mid.ValueKind == JsonValueKind.String && mid.GetString()?.Length > 0;
            if (!accepted) throw new InvalidDataException("Сервис не подтвердил отправку сообщения.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { throw new InvalidOperationException("Не удалось отправить оповещение. Проверьте сеть, токен бота и ID получателя.", error); }
    }
    public static string Format(BackupRun run)
    {
        // Do not send raw errors, source paths, connection strings or credentials to messenger servers.
        var name = new string(run.JobName.Where(c => !char.IsControl(c)).Take(500).ToArray());
        var machine = new string(Environment.MachineName.Take(100).ToArray());
        return $"1C-Backup · {machine}\nЗадание: {name}\nРезультат: {run.StatusDescription}\nНачало: {run.StartedUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}\nОкончание: {run.FinishedUtc?.ToLocalTime():dd.MM.yyyy HH:mm:ss}\nПредупреждений: {run.Warnings?.Count ?? 0}\nПодробности — в истории запусков программы.";
    }
}
/// <summary>Durable delivery receipts; terminal history is the queue. Shares a separate lease across UI/service processes.</summary>
public sealed class BackupNotificationDispatcher(string directory, IBackupNotificationSender sender, TimeProvider? clock = null)
{
    private sealed record Receipt(Guid ChannelId, Guid RunId);
    private readonly AgentJsonFile<List<Receipt>> receipts = new(Path.Combine(directory, "notification-deliveries.json"));
    private readonly Dictionary<Guid, DateTimeOffset> retryAt = [];
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<string?> PollAsync(CancellationToken token = default)
    {
        if (!await gate.WaitAsync(0, token)) return null;
        try
        {
            var settings = await new NotificationSettingsStore(directory).LoadAsync(token);
            var channels = settings.Channels.Where(c => c.Enabled).ToArray();
            if (channels.Length == 0) return null;
            using var lease = new BackupEngineCoordinator(Path.Combine(directory, "notification-control")).Acquire();
            var runs = await new BackupRunHistory(Path.Combine(directory, "runs.json")).ReadAsync(token);
            var sent = await receipts.ReadAsync(token) ?? [];
            if (sent.Any(r => r is null || r.ChannelId == Guid.Empty || r.RunId == Guid.Empty)) throw new InvalidDataException("Некорректная история оповещений.");
            var known = sent.ToHashSet(); var notices = new List<string>();
            var now = (clock ?? TimeProvider.System).GetUtcNow();
            foreach (var channel in channels)
            {
                var name = channel.Provider == NotificationProvider.Max ? "MAX" : "Telegram";
                var pending = runs.Where(r => r.FinishedUtc >= channel.EnabledSinceUtc && settings.Includes(r)
                    && !known.Contains(new(channel.Id, r.RunId))).OrderBy(r => r.FinishedUtc).Take(2);
                if (retryAt.TryGetValue(channel.Id, out var next) && next > now)
                { notices.Add(name + ": отправка отложена, будет повторена автоматически."); continue; }
                foreach (var run in pending)
                {
                    try
                    {
                        await sender.SendAsync(channel, BotNotificationSender.Format(run), token);
                        var receipt = new Receipt(channel.Id, run.RunId); known.Add(receipt);
                        var currentIds = runs.Select(r => r.RunId).ToHashSet();
                        sent = known.Where(r => currentIds.Contains(r.RunId) && channels.Any(c => c.Id == r.ChannelId)).ToList();
                        await receipts.WriteAsync(sent, token);
                        retryAt.Remove(channel.Id);
                        DiagnosticLog.Current.Write(DiagnosticEvent.NotificationSent, jobId: run.JobId, runId: run.RunId);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        retryAt[channel.Id] = now.AddMinutes(1);
                        DiagnosticLog.Current.Write(DiagnosticEvent.NotificationFailed, DiagnosticLevel.Warning, error, jobId: run.JobId, runId: run.RunId);
                        notices.Add(name + ": оповещение не доставлено; повтор через минуту. Проверьте подключение и настройки."); break;
                    }
                }
            }
            return notices.Count == 0 ? null : string.Join("\n", notices);
        }
        catch (BackupEngineBusyException) { return null; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            DiagnosticLog.Current.Write(DiagnosticEvent.NotificationFailed, DiagnosticLevel.Warning, error);
            return "Оповещения недоступны. Проверьте настройки и доступ к рабочему профилю; резервное копирование продолжается.";
        }
        finally { gate.Release(); }
    }
}
