using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SPBackup.Core;

public enum NotificationDeliveryState { Pending, Sending, Retry, Unknown, Blocked, Accepted, Expired, Cancelled }

public sealed record NotificationQueueEntry(Guid ChannelId, Guid RunId, NotificationProvider? Provider,
    NotificationDeliveryState State, DateTimeOffset FinishedUtc, int Attempts, DateTimeOffset? NextAttemptUtc,
    DateTimeOffset? AcceptedUtc, NotificationFailure? Failure, bool CanRetry, bool CanCancel);

public sealed record NotificationQueueView(int Outstanding, IReadOnlyList<NotificationQueueEntry> Entries);

internal sealed record NotificationDelivery(
    Guid ChannelId, Guid RunId, Guid JobId, string Route, string Summary, DateTimeOffset FinishedUtc)
{
    public NotificationDeliveryState State { get; set; }
    public NotificationProvider? Provider { get; init; }
    public bool ManualRetry { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? QueuedUtc { get; init; }
    public DateTimeOffset? LastAttemptUtc { get; set; }
    public DateTimeOffset? AcceptedUtc { get; set; }
    public DateTimeOffset? NextAttemptUtc { get; set; }
    public NotificationFailure? Failure { get; set; }
    public string? BlockedCredential { get; set; }
    public string? MessageId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsOpen => State is NotificationDeliveryState.Pending or NotificationDeliveryState.Sending or
        NotificationDeliveryState.Retry or NotificationDeliveryState.Unknown or NotificationDeliveryState.Blocked;
}

public sealed class BackupNotificationQueue(string directory)
{
    internal const int PendingLimit = 10_000;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromHours(72);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private static readonly TimeSpan[] Delays =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30)];
    private const string LegacyWarning = "Не удалось записать подтверждения для предыдущей версии агента.";
    private sealed record Receipt(Guid ChannelId, Guid RunId);
    private sealed record Snapshot(int Version, List<NotificationDelivery> Deliveries)
    {
        public bool LegacyImported { get; set; }
    }
    private readonly AgentJsonFile<Snapshot> queue = new(Path.Combine(directory, "notification-queue.json"));
    private readonly AgentJsonFile<List<Receipt>> legacy = new(Path.Combine(directory, "notification-deliveries.json"));

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string Route(NotificationChannel channel) => Hash($"{channel.Provider}/{channel.MaxUser}/{channel.Recipient}");

    public async Task<NotificationQueueView> ViewAsync(CancellationToken token = default)
    {
        var snapshot = await ReadAsync(token);
        var settings = await new NotificationSettingsStore(directory).LoadAsync(token);
        var active = settings.Channels.Where(c => c.Enabled).ToDictionary(c => c.Id);
        var now = DateTimeOffset.UtcNow;
        var routes = active.Values.ToDictionary(channel => channel.Id, Route);
        var pausedChannels = snapshot.Deliveries.Where(item => item.Failure == NotificationFailure.RateLimited && item.NextAttemptUtc > now
            && active.TryGetValue(item.ChannelId, out var channel) && Matches(item, channel, routes[channel.Id]))
            .Select(item => item.ChannelId).ToHashSet();
        var entries = snapshot.Deliveries.OrderByDescending(item => item.IsOpen)
            .ThenByDescending(item => item.FinishedUtc).ThenBy(item => item.RunId).Take(100)
            .Select(item =>
            {
                active.TryGetValue(item.ChannelId, out var channel);
                var actionable = channel is not null && Matches(item, channel, routes[channel.Id]) && now - item.FinishedUtc < Lifetime;
                var rateLimited = pausedChannels.Contains(item.ChannelId);
                return new NotificationQueueEntry(item.ChannelId, item.RunId, item.Provider ?? channel?.Provider, item.State,
                    item.FinishedUtc, item.Attempts, item.NextAttemptUtc, item.AcceptedUtc, item.Failure,
                    actionable && !rateLimited && CanRetry(item), actionable && item.IsOpen && item.State != NotificationDeliveryState.Sending);
            }).ToArray();
        return new(snapshot.Deliveries.Count(item => item.IsOpen), entries);
    }

    public Task RetryAsync(Guid channelId, Guid runId, CancellationToken token = default) =>
        ChangeAsync(channelId, runId, retry: true, token);

    public Task CancelAsync(Guid channelId, Guid runId, CancellationToken token = default) =>
        ChangeAsync(channelId, runId, retry: false, token);

    private async Task ChangeAsync(Guid channelId, Guid runId, bool retry, CancellationToken token)
    {
        using var lease = await AcquireAsync(token);
        var snapshot = await ReadAsync(token);
        var item = snapshot.Deliveries.SingleOrDefault(entry => entry.ChannelId == channelId && entry.RunId == runId);
        var settings = await new NotificationSettingsStore(directory).LoadAsync(token);
        var channel = settings.Channels.SingleOrDefault(c => c.Id == channelId && c.Enabled);
        var now = DateTimeOffset.UtcNow;
        if (item is null || channel is null || !Matches(item, channel, Route(channel)) || !item.IsOpen ||
            item.State == NotificationDeliveryState.Sending || now - item.FinishedUtc >= Lifetime ||
            (await ReadReceiptsAsync(token)).Contains(new(channelId, runId)))
            throw new InvalidOperationException("Состояние доставки изменилось. Обновите очередь.");

        if (retry)
        {
            var route = Route(channel);
            if (!CanRetry(item) || snapshot.Deliveries.Any(entry => entry.Failure == NotificationFailure.RateLimited
                && entry.NextAttemptUtc > now && Matches(entry, channel, route)))
                throw new InvalidOperationException("Повтор пока недоступен. Дождитесь окончания паузы сервиса.");
            item.State = NotificationDeliveryState.Pending;
            item.ManualRetry = true;
            item.NextAttemptUtc = null;
            item.Failure = null;
            item.BlockedCredential = null;
        }
        else
        {
            item.State = NotificationDeliveryState.Cancelled;
            item.ManualRetry = false;
            // Preserve a provider cooldown so another item does not bypass it.
        }
        await queue.WriteAsync(snapshot, token);
    }

    private static bool Matches(NotificationDelivery item, NotificationChannel channel, string route) =>
        item.ChannelId == channel.Id && item.Route == route && item.FinishedUtc >= channel.EnabledSinceUtc;

    private static bool CanRetry(NotificationDelivery item) => item.State is
        NotificationDeliveryState.Retry or NotificationDeliveryState.Unknown or NotificationDeliveryState.Blocked;

    internal static async Task CaptureAsync(string historyPath, IReadOnlyList<BackupRun> runs, CancellationToken token)
    {
        if (!string.Equals(Path.GetFileName(historyPath), "runs.json", StringComparison.OrdinalIgnoreCase)) return;
        var directory = Path.GetDirectoryName(Path.GetFullPath(historyPath))!;
        try
        {
            if (!File.Exists(Path.Combine(directory, "notifications.json"))) return;
            var settings = await new NotificationSettingsStore(directory).LoadAsync(token);
            if (!settings.Channels.Any(c => c.Enabled) || !runs.Any(settings.Includes)) return;
            var store = new BackupNotificationQueue(directory);
            using var lease = await store.AcquireAsync(token);
            var snapshot = await store.ReadAsync(token);
            var receipts = await store.ReadReceiptsAsync(token);
            var (added, full) = Enqueue(snapshot, settings, runs, DateTimeOffset.UtcNow, receipts);
            if (added) await store.queue.WriteAsync(snapshot, token);
            if (full) DiagnosticLog.Current.Write(DiagnosticEvent.NotificationFailed, DiagnosticLevel.Warning,
                new InvalidOperationException("Очередь оповещений заполнена. Результат сохранён в истории."));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { /* History is committed. */ }
        catch (Exception)
        {
            DiagnosticLog.Current.Write(DiagnosticEvent.NotificationFailed, DiagnosticLevel.Warning,
                new InvalidOperationException("Оповещение ещё не перенесено в очередь. Оно будет восстановлено из истории."));
        }
    }

    internal async Task<string?> PrepareAsync(NotificationSettings settings, IReadOnlyList<BackupRun> runs,
        DateTimeOffset now, CancellationToken token)
    {
        using var lease = await AcquireAsync(token);
        var snapshot = await ReadAsync(token);
        HashSet<Receipt>? receipts;
        try { receipts = await ReadReceiptsAsync(token); }
        catch (Exception error) when (snapshot.LegacyImported && IsStorageFailure(error)) { receipts = null; }
        var dirty = !snapshot.LegacyImported;
        snapshot.LegacyImported = true;
        var activeRoutes = settings.Channels.Where(c => c.Enabled).ToDictionary(c => c.Id, c => (Route: Route(c), c.EnabledSinceUtc));
        var missingReceipts = new List<Receipt>();
        foreach (var item in snapshot.Deliveries)
        {
            var key = new Receipt(item.ChannelId, item.RunId);
            if (item.State == NotificationDeliveryState.Accepted)
            {
                if (receipts?.Contains(key) != true) missingReceipts.Add(key);
                continue;
            }
            if (receipts?.Contains(key) == true)
            {
                item.State = NotificationDeliveryState.Accepted;
                item.NextAttemptUtc = null;
                item.Failure = null;
                item.BlockedCredential = null;
                dirty = true;
            }
            else if (item.IsOpen && (!activeRoutes.TryGetValue(item.ChannelId, out var route) || item.Route != route.Route
                || item.FinishedUtc < route.EnabledSinceUtc))
            { item.State = NotificationDeliveryState.Cancelled; dirty = true; }
            else if (item.IsOpen && now - item.FinishedUtc >= Lifetime)
            { item.State = NotificationDeliveryState.Expired; dirty = true; }
            else if (item.State == NotificationDeliveryState.Sending && receipts is not null)
            {
                item.State = NotificationDeliveryState.Unknown;
                item.Failure = NotificationFailure.OutcomeUnknown;
                item.NextAttemptUtc = now.Add(Delay(item.Attempts));
                dirty = true;
            }
        }
        var legacyWritten = receipts is not null && (missingReceipts.Count == 0 || await TryMirrorAsync(receipts, missingReceipts, token));
        dirty |= snapshot.Deliveries.RemoveAll(item => !item.IsOpen && now - item.FinishedUtc >= Retention
            && !(item.Failure == NotificationFailure.RateLimited && item.NextAttemptUtc > now)
            && (item.State != NotificationDeliveryState.Accepted || legacyWritten)) > 0;
        if (legacyWritten)
        {
            var olderAccepted = snapshot.Deliveries.Where(item => item.State == NotificationDeliveryState.Accepted)
                .OrderByDescending(item => item.FinishedUtc).Skip(1000).ToHashSet();
            dirty |= snapshot.Deliveries.RemoveAll(olderAccepted.Contains) > 0;
        }
        var (added, full) = receipts is null ? (false, false) : Enqueue(snapshot, settings, runs, now, receipts);
        if (dirty || added) await queue.WriteAsync(snapshot, token);
        if (!legacyWritten) return LegacyWarning;
        return full ? "Очередь оповещений заполнена. Оставшиеся результаты сохранены в истории." : null;
    }

    internal async Task<(NotificationDelivery? Item, string? Notice)> ClaimAsync(
        NotificationChannel channel, DateTimeOffset now, CancellationToken token)
    {
        using var lease = await AcquireAsync(token);
        var snapshot = await ReadAsync(token);
        var route = Route(channel);
        var deliveries = snapshot.Deliveries.Where(item => Matches(item, channel, route));
        if (deliveries.Any(item => item.Failure == NotificationFailure.RateLimited && item.NextAttemptUtc > now))
            return (null, "Сервис ограничил частоту. Повтор отложен.");
        var item = deliveries.Where(item => item.IsOpen && now - item.FinishedUtc < Lifetime)
            .OrderByDescending(item => item.ManualRetry).ThenBy(item => item.FinishedUtc).ThenBy(item => item.RunId).FirstOrDefault();
        if (item is null) return (null, null);
        if (item.State == NotificationDeliveryState.Sending)
            return (null, "Исход предыдущей попытки неизвестен. Проверьте доступ к подтверждениям.");
        if (item.State == NotificationDeliveryState.Blocked && item.BlockedCredential == Hash(channel.CredentialKey))
            return (null, new NotificationSendException(item.Failure!.Value).Message);
        if (item.NextAttemptUtc > now)
            return (null, item.State == NotificationDeliveryState.Unknown
                ? "Исход предыдущей попытки неизвестен. Повтор отложен." : "Повтор отложен.");
        item.State = NotificationDeliveryState.Sending;
        item.ManualRetry = false;
        item.Attempts = Math.Min(item.Attempts + 1, int.MaxValue - 1);
        item.LastAttemptUtc = now;
        item.NextAttemptUtc = null;
        item.Failure = null;
        item.BlockedCredential = null;
        await queue.WriteAsync(snapshot, token);
        return (item, null);
    }

    internal async Task<string?> CompleteAsync(NotificationChannel channel, NotificationDelivery claimed,
        NotificationAcceptance? accepted, NotificationSendException? failure, DateTimeOffset now)
    {
        var token = CancellationToken.None;
        using var lease = await AcquireAsync(token);
        var snapshot = await ReadAsync(token);
        var item = snapshot.Deliveries.Single(d => d.ChannelId == claimed.ChannelId && d.RunId == claimed.RunId);
        if (item.State != NotificationDeliveryState.Sending || item.Attempts != claimed.Attempts)
            throw new InvalidDataException("Попытка оповещения уже завершена.");
        if (accepted is not null)
        {
            item.State = NotificationDeliveryState.Accepted;
            item.MessageId = accepted.MessageId;
            item.AcceptedUtc = now;
            var mirrored = await TryMirrorAsync(null, [new(item.ChannelId, item.RunId)], token);
            await queue.WriteAsync(snapshot, token);
            return mirrored ? null : LegacyWarning;
        }

        item.Failure = failure?.Failure ?? NotificationFailure.OutcomeUnknown;
        var detail = new NotificationSendException(item.Failure.Value).Message;
        if (item.Failure is NotificationFailure.Authorization or NotificationFailure.CredentialUnavailable or NotificationFailure.RecipientRejected or NotificationFailure.InvalidToken)
        {
            item.State = NotificationDeliveryState.Blocked;
            item.BlockedCredential = Hash(channel.CredentialKey);
        }
        else
        {
            item.State = item.Failure == NotificationFailure.OutcomeUnknown ? NotificationDeliveryState.Unknown : NotificationDeliveryState.Retry;
            var delay = Delay(item.Attempts);
            if (failure?.RetryAfter is { } requested && requested > delay) delay = requested;
            item.NextAttemptUtc = now.Add(delay);
            detail += " Будет повторено автоматически.";
        }
        await queue.WriteAsync(snapshot, token);
        return detail;
    }

    private static TimeSpan Delay(int attempts) => Delays[Math.Clamp(attempts - 1, 0, Delays.Length - 1)];

    private static (bool Added, bool Full) Enqueue(Snapshot snapshot, NotificationSettings settings,
        IReadOnlyList<BackupRun> runs, DateTimeOffset now, HashSet<Receipt> receipts)
    {
        var known = snapshot.Deliveries.Select(item => new Receipt(item.ChannelId, item.RunId)).ToHashSet();
        var remaining = PendingLimit - snapshot.Deliveries.Count(item => item.IsOpen);
        var channels = settings.Channels.Where(c => c.Enabled).Select(c => (Channel: c, Route: Route(c))).ToArray();
        var added = false;
        foreach (var run in runs.Where(run => run.FinishedUtc is { } finished && now - finished < Lifetime && settings.Includes(run))
                     .OrderBy(run => run.FinishedUtc).ThenBy(run => run.RunId))
        {
            foreach (var (channel, route) in channels)
            {
                var key = new Receipt(channel.Id, run.RunId);
                if (run.FinishedUtc < channel.EnabledSinceUtc || receipts.Contains(key) || !known.Add(key)) continue;
                if (remaining <= 0) return (added, true);
                snapshot.Deliveries.Add(new(channel.Id, run.RunId, run.JobId, route, BotNotificationSender.Format(run), run.FinishedUtc!.Value)
                { QueuedUtc = now, Provider = channel.Provider });
                remaining--;
                added = true;
            }
        }
        return (added, false);
    }

    private async Task<Snapshot> ReadAsync(CancellationToken token)
    {
        var snapshot = await queue.ReadBoundedAsync(128L * 1024 * 1024, token) ?? new(1, []);
        if (snapshot is not { Version: 1, Deliveries: { } deliveries }
            || deliveries.Any(d => d is null || d.ChannelId == Guid.Empty || d.RunId == Guid.Empty || d.JobId == Guid.Empty
                || d.FinishedUtc == default || d.Route is not { Length: 64 } || d.Summary is not { Length: > 0 and <= 3900 }
                || d.Provider is { } provider && !Enum.IsDefined(provider)
                || d.Attempts < 0 || d.Attempts == int.MaxValue || !Enum.IsDefined(d.State)
                || d.MessageId is { Length: 0 or > 256 } || d.State == NotificationDeliveryState.Sending && d.Attempts == 0
                || d.Failure is { } failure && !Enum.IsDefined(failure)
                || d.State == NotificationDeliveryState.Blocked && (d.Failure is null || d.BlockedCredential is not { Length: 64 }))
            || deliveries.Select(d => (d.ChannelId, d.RunId)).Distinct().Count() != deliveries.Count)
            throw new InvalidDataException("Некорректная очередь оповещений.");
        return snapshot;
    }

    private async Task<HashSet<Receipt>> ReadReceiptsAsync(CancellationToken token)
    {
        var receipts = await legacy.ReadAsync(token) ?? [];
        if (receipts.Any(r => r is null || r.ChannelId == Guid.Empty || r.RunId == Guid.Empty))
            throw new InvalidDataException("Некорректная история оповещений.");
        return receipts.ToHashSet();
    }

    private async Task<bool> TryMirrorAsync(HashSet<Receipt>? receipts, IReadOnlyList<Receipt> added, CancellationToken token)
    {
        try
        {
            receipts ??= await ReadReceiptsAsync(token);
            receipts.UnionWith(added);
            await legacy.WriteAsync(receipts.ToList(), token);
            return true;
        }
        catch (Exception error) when (IsStorageFailure(error)) { return false; }
    }

    private static bool IsStorageFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException;

    private async Task<IDisposable> AcquireAsync(CancellationToken token)
    {
        var coordinator = new BackupEngineCoordinator(Path.Combine(directory, "notification-queue-control"));
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { return coordinator.Acquire(); }
            catch (BackupEngineBusyException) when (attempt < 20) { await Task.Delay(25, token); }
        }
    }
}
