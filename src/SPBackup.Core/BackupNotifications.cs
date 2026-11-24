using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SPBackup.Core;

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
public sealed record NotificationAcceptance(string MessageId);

public enum NotificationFailure
{
    CredentialUnavailable, Authorization, RecipientRejected, RateLimited, Temporary,
    Dns, Tls, OutcomeUnknown, UnexpectedResponse
}

public sealed class NotificationSendException(NotificationFailure failure, TimeSpan? retryAfter = null)
    : InvalidOperationException(failure switch
    {
        NotificationFailure.CredentialUnavailable => "Токен бота недоступен. Сохраните его заново и проверьте доступ исполнителя.",
        NotificationFailure.Authorization => "Сервис отклонил авторизацию. Проверьте токен и права бота.",
        NotificationFailure.RecipientRejected => "Получатель отклонён. Проверьте ID и доступ бота к чату.",
        NotificationFailure.RateLimited => "Сервис ограничил частоту оповещений.",
        NotificationFailure.Temporary => "Сервис оповещений временно недоступен.",
        NotificationFailure.Dns => "Не удалось определить адрес сервиса оповещений. Проверьте сеть и DNS.",
        NotificationFailure.Tls => "Не удалось установить защищённое соединение с сервисом оповещений. Проверьте доверие к сертификатам.",
        NotificationFailure.OutcomeUnknown => "Подтверждение от сервиса не получено. Сообщение могло быть принято.",
        _ => "Сервис вернул неожиданный ответ. Отправка не подтверждена."
    })
{
    public NotificationFailure Failure { get; } = failure;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public interface IBackupNotificationSender
{
    Task<NotificationAcceptance> SendAsync(NotificationChannel channel, string text, CancellationToken token = default);
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
    public async Task<NotificationAcceptance> SendAsync(NotificationChannel channel, string text, CancellationToken token = default)
    {
        if (!NotificationSettings.ValidRecipient(channel) || !Enum.IsDefined(channel.Provider)) throw new InvalidDataException("Некорректный получатель оповещений.");
        if (text.Length is 0 or > 3900) throw new ArgumentException("Сообщение должно содержать до 3900 символов.");
        string secret;
        try { secret = (await credentials.GetAsync(channel.CredentialKey, token)).Password; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (Exception) { throw new NotificationSendException(NotificationFailure.CredentialUnavailable); }
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
            if (!response.IsSuccessStatusCode)
                throw await RejectResponseAsync(response, channel.Provider, deadline.Token, token);

            using var json = await ReadResponseAsync(response.Content, deadline.Token);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (channel.Provider == NotificationProvider.Telegram && root.TryGetProperty("ok", out var ok))
                {
                    if (ok.ValueKind == JsonValueKind.True && ObjectProperty(root, "result", out var result)
                        && IntegerProperty(result, "message_id", out var messageId) && messageId > 0)
                        return new(messageId.ToString(CultureInfo.InvariantCulture));
                    if (ok.ValueKind == JsonValueKind.False && IntegerProperty(root, "error_code", out var telegramStatus)
                        && telegramStatus is >= 400 and <= 599)
                        throw TelegramRejection((int)telegramStatus, root, RetryAfter(response));
                }
                if (channel.Provider == NotificationProvider.Max && ObjectProperty(root, "message", out var message)
                    && ObjectProperty(message, "body", out var body) && body.TryGetProperty("mid", out var mid)
                    && mid.ValueKind == JsonValueKind.String && mid.GetString() is { Length: > 0 and <= 256 } id
                    && !string.IsNullOrWhiteSpace(id) && !id.Any(char.IsControl))
                    return new(id);
            }
            throw new NotificationSendException(NotificationFailure.UnexpectedResponse);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (NotificationSendException) { throw; }
        catch (JsonException) { throw new NotificationSendException(NotificationFailure.UnexpectedResponse); }
        catch (HttpRequestException error)
        {
            // The Telegram URI contains the token. Never retain the exception or the response body.
            throw new NotificationSendException(error.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => NotificationFailure.Dns,
                HttpRequestError.SecureConnectionError => NotificationFailure.Tls,
                _ => NotificationFailure.OutcomeUnknown
            });
        }
        catch (Exception) { throw new NotificationSendException(NotificationFailure.OutcomeUnknown); }
    }

    private static async Task<NotificationSendException> RejectResponseAsync(HttpResponseMessage response,
        NotificationProvider provider, CancellationToken deadline, CancellationToken callerToken)
    {
        var status = (int)response.StatusCode;
        var retry = status == 429 ? RetryAfter(response) : null;
        if (provider == NotificationProvider.Telegram ? status is 400 or 429 : status is 400 or 403 or 404)
        {
            try
            {
                using var json = await ReadResponseAsync(response.Content, deadline);
                var root = json.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (provider == NotificationProvider.Telegram) return TelegramRejection(status, root, retry);
                    if (StringPropertyIs(root, "code", "chat.denied")) return new(NotificationFailure.RecipientRejected);
                }
            }
            catch (Exception) when (!callerToken.IsCancellationRequested)
            { /* Keep the HTTP classification if the optional error body cannot be read. */ }
        }
        callerToken.ThrowIfCancellationRequested();
        return Rejected(status, retry);
    }

    private static NotificationSendException TelegramRejection(int status, JsonElement root, TimeSpan? retry)
    {
        if (status == 400 && StringPropertyIs(root, "description", "Bad Request: chat not found"))
            return new(NotificationFailure.RecipientRejected);
        if (status == 429 && TelegramRetryAfter(root) is { } delay && (retry is null || delay > retry))
            retry = delay;
        return Rejected(status, retry);
    }

    private static bool StringPropertyIs(JsonElement root, string name, string expected) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.ValueEquals(expected);

    private static NotificationSendException Rejected(int status, TimeSpan? retry = null) => status switch
    {
        401 or 403 => new(NotificationFailure.Authorization),
        429 => new(NotificationFailure.RateLimited, retry),
        408 => new(NotificationFailure.OutcomeUnknown),
        >= 500 and <= 599 => new(NotificationFailure.Temporary),
        _ => new(NotificationFailure.UnexpectedResponse)
    };

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var delay = header?.Delta ?? header?.Date - DateTimeOffset.UtcNow;
        return delay > TimeSpan.Zero && delay <= TimeSpan.FromDays(365) ? delay : null;
    }

    private static TimeSpan? TelegramRetryAfter(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && ObjectProperty(root, "parameters", out var parameters)
        && IntegerProperty(parameters, "retry_after", out var seconds) && seconds is > 0 and <= 31_536_000
            ? TimeSpan.FromSeconds(seconds) : null;

    private static bool ObjectProperty(JsonElement root, string name, out JsonElement value) =>
        root.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;

    private static bool IntegerProperty(JsonElement root, string name, out long value)
    {
        value = 0;
        return root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out value);
    }

    private static async Task<JsonDocument> ReadResponseAsync(HttpContent content, CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token);
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (body.Length + count > 65536) throw new NotificationSendException(NotificationFailure.UnexpectedResponse);
            body.Write(buffer, 0, count);
        }
        body.Position = 0;
        return await JsonDocument.ParseAsync(body, cancellationToken: token);
    }

    public static string Format(BackupRun run)
    {
        // Do not send raw errors, source paths, connection strings or credentials to messenger servers.
        var name = new string(run.JobName.Where(c => !char.IsControl(c)).Take(500).ToArray());
        var machine = new string(Environment.MachineName.Take(100).ToArray());
        return $"SP Backup · {machine}\nЗадание: {name}\nРезультат: {run.StatusDescription}\nНачало: {run.StartedUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}\nОкончание: {run.FinishedUtc?.ToLocalTime():dd.MM.yyyy HH:mm:ss}\nПредупреждений: {run.Warnings?.Count ?? 0}\nПодробности — в истории запусков программы.";
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
                        var failure = error as NotificationSendException;
                        var delay = failure?.RetryAfter is { } requested && requested > TimeSpan.FromMinutes(1)
                            ? requested : TimeSpan.FromMinutes(1);
                        retryAt[channel.Id] = (clock ?? TimeProvider.System).GetUtcNow().Add(delay);
                        DiagnosticLog.Current.Write(DiagnosticEvent.NotificationFailed, DiagnosticLevel.Warning, failure, jobId: run.JobId, runId: run.RunId);
                        var reason = failure?.Message ?? "Отправка или запись подтверждения не завершилась.";
                        notices.Add(name + ": " + reason + " Будет повторено автоматически.");
                        break;
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
