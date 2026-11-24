using System.Net;
using System.Text;
using System.Text.Json;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class BackupNotificationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OneCBackup-notifications-" + Guid.NewGuid());
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow.AddMinutes(-1);
    public BackupNotificationTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private static NotificationChannel Channel(NotificationProvider provider = NotificationProvider.Telegram) => new()
        { Provider = provider, Enabled = true, CredentialKey = "fixture", Recipient = "-100123", EnabledSinceUtc = Now.AddDays(-1) };
    private static BackupRun Run(BackupRunStatus status = BackupRunStatus.Failed) => new(Guid.NewGuid(), Guid.NewGuid(), "Тест <база>", Now.AddMinutes(-1), Now,
        status, "private-work", Error: "password=secret; C:\\private\\base", Warnings: status == BackupRunStatus.Succeeded ? ["private-warning"] : null);
    private sealed class Secrets(string password) : IFtpCredentialStore
    { public Task<NetworkCredential> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(new NetworkCredential("bot", password)); }
    private sealed class Api(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => action(request, token); }
    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    [Theory]
    [InlineData(NotificationProvider.Telegram, false)]
    [InlineData(NotificationProvider.Max, false)]
    [InlineData(NotificationProvider.Max, true)]
    public async Task UsesOfficialHttpsEndpointsAndPlainText(NotificationProvider provider, bool maxUser)
    {
        const string secret = "123456:fixture_secret";
        var channel = Channel(provider) with { MaxUser = maxUser, Recipient = maxUser ? "12345" : "-100123" };
        using var client = new HttpClient(new Api(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("https", request.RequestUri!.Scheme);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("Тест <база>", body.RootElement.GetProperty("text").GetString());
            Assert.False(body.RootElement.TryGetProperty("parse_mode", out _)); Assert.False(body.RootElement.TryGetProperty("format", out _));
            if (provider == NotificationProvider.Telegram)
            {
                Assert.Equal("api.telegram.org", request.RequestUri.Host); Assert.Equal("/bot" + secret + "/sendMessage", request.RequestUri.AbsolutePath);
                Assert.Equal(channel.Recipient, body.RootElement.GetProperty("chat_id").GetString());
                return Json("{\"ok\":true,\"result\":{\"message_id\":1}}");
            }
            Assert.Equal("platform-api2.max.ru", request.RequestUri.Host);
            Assert.Equal("?" + (maxUser ? "user_id=" : "chat_id=") + channel.Recipient, request.RequestUri.Query);
            Assert.Equal(secret, Assert.Single(request.Headers.GetValues("Authorization")));
            Assert.DoesNotContain(secret, request.RequestUri.AbsoluteUri);
            return Json("{\"message\":{\"body\":{\"mid\":\"fixture\"}}}");
        }));
        var accepted = await new BotNotificationSender(client, new Secrets(secret)).SendAsync(channel, "Тест <база>");
        Assert.Equal(provider == NotificationProvider.Telegram ? "1" : "fixture", accepted.MessageId);
    }
    [Theory]
    [InlineData(NotificationProvider.Telegram, "{\"ok\":false,\"description\":\"TOKEN_private\"}", 200)]
    [InlineData(NotificationProvider.Max, "{\"code\":\"error\",\"message\":\"TOKEN_private\"}", 200)]
    [InlineData(NotificationProvider.Telegram, "TOKEN_private", 403)]
    [InlineData(NotificationProvider.Max, "TOKEN_private", 429)]
    [InlineData(NotificationProvider.Telegram, "[]", 200)]
    public async Task RejectedOrInvalidResponseIsNotSuccessAndDoesNotExposeApiText(NotificationProvider provider, string body, int status)
    {
        using var client = new HttpClient(new Api((_, _) => Task.FromResult(Json(body, (HttpStatusCode)status))));
        var error = await Assert.ThrowsAsync<NotificationSendException>(() => new BotNotificationSender(client, new Secrets("123:fixture")).SendAsync(Channel(provider), "test"));
        Assert.DoesNotContain("TOKEN_private", error.Message); Assert.DoesNotContain("123:fixture", error.Message);
    }
    [Theory]
    [InlineData(NotificationProvider.Telegram, 400, "{\"ok\":false,\"description\":\"Bad Request: chat not found\"}", NotificationFailure.RecipientRejected)]
    [InlineData(NotificationProvider.Max, 403, "{\"code\":\"chat.denied\",\"message\":\"secret\"}", NotificationFailure.RecipientRejected)]
    [InlineData(NotificationProvider.Max, 400, "{\"code\":\"proto.payload\"}", NotificationFailure.UnexpectedResponse)]
    [InlineData(NotificationProvider.Telegram, 401, "secret", NotificationFailure.Authorization)]
    [InlineData(NotificationProvider.Max, 503, "secret", NotificationFailure.Temporary)]
    [InlineData(NotificationProvider.Telegram, 200, "{\"ok\": true, \"result\": {\"message_id\": \"1\"}}", NotificationFailure.UnexpectedResponse)]
    [InlineData(NotificationProvider.Max, 200, "{\"message\": {\"body\": {\"mid\": \" \"}}}", NotificationFailure.UnexpectedResponse)]
    public async Task ProviderFailureIsClassifiedWithoutApiDetails(NotificationProvider provider, int status, string body, NotificationFailure expected)
    {
        using var client = new HttpClient(new Api((_, _) => Task.FromResult(Json(body, (HttpStatusCode)status))));
        var error = await Assert.ThrowsAsync<NotificationSendException>(() =>
            new BotNotificationSender(client, new Secrets("123:fixture")).SendAsync(Channel(provider), "test"));
        Assert.Equal(expected, error.Failure);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("secret", error.ToString());
    }

    [Theory]
    [InlineData(NotificationProvider.Telegram, 429, "{\"parameters\":{\"retry_after\":180}}", 60, 180)]
    [InlineData(NotificationProvider.Telegram, 200, "{\"ok\":false,\"error_code\":429,\"parameters\":{\"retry_after\":60}}", 100, 100)]
    [InlineData(NotificationProvider.Telegram, 429, "invalid JSON", 90, 90)]
    [InlineData(NotificationProvider.Max, 429, "invalid JSON", 90, 90)]
    [InlineData(NotificationProvider.Max, 429, "invalid JSON", 0, null)]
    public async Task RateLimitRespectsRetryAfter(NotificationProvider provider, int status, string body, int header, int? expected)
    {
        using var client = new HttpClient(new Api((_, _) =>
        {
            var response = Json(body, (HttpStatusCode)status);
            if (header > 0) response.Headers.RetryAfter = new(TimeSpan.FromSeconds(header));
            return Task.FromResult(response);
        }));
        var error = await Assert.ThrowsAsync<NotificationSendException>(() =>
            new BotNotificationSender(client, new Secrets("123:fixture")).SendAsync(Channel(provider), "test"));
        Assert.Equal(NotificationFailure.RateLimited, error.Failure);
        Assert.Equal(expected.HasValue ? TimeSpan.FromSeconds(expected.Value) : (TimeSpan?)null, error.RetryAfter);
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, NotificationFailure.Dns)]
    [InlineData(HttpRequestError.SecureConnectionError, NotificationFailure.Tls)]
    [InlineData(HttpRequestError.ConnectionError, NotificationFailure.OutcomeUnknown)]
    public async Task TransportExceptionDoesNotRetainTheRequestUri(HttpRequestError error, NotificationFailure expected)
    {
        using var client = new HttpClient(new Api((request, _) =>
            throw new HttpRequestException(error, request.RequestUri!.AbsoluteUri)));
        var failure = await Assert.ThrowsAsync<NotificationSendException>(() =>
            new BotNotificationSender(client, new Secrets("123:fixture")).SendAsync(Channel(), "test"));
        Assert.Equal(expected, failure.Failure);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("123:fixture", failure.ToString());
    }

    [Theory]
    [InlineData("123:abc/other")]
    [InlineData("123:abc?query=1")]
    [InlineData("123:abc\r\nHeader: secret")]
    public async Task InvalidTokenCannotChangeRequestDestination(string secret)
    {
        using var client = new HttpClient(new Api((_, _) => throw new InvalidOperationException("Must not request")));
        await Assert.ThrowsAsync<InvalidDataException>(() => new BotNotificationSender(client, new Secrets(secret)).SendAsync(Channel(), "test"));
    }
    [Fact]
    public async Task CancellationStopsNetworkRequest()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new Api((request, token) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(request.RequestUri!.AbsoluteUri, token);
        }));
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new BotNotificationSender(client, new Secrets("123:fixture")).SendAsync(Channel(), "test", cancellation.Token));
        Assert.Equal(cancellation.Token, cancelled.CancellationToken);
        Assert.DoesNotContain("123:fixture", cancelled.ToString());
    }
    private sealed class Sender : IBackupNotificationSender
    {
        public List<NotificationProvider> Sent { get; } = [];
        public Exception? TelegramFailure;
        public Action? AfterSend;
        public Task<NotificationAcceptance> SendAsync(NotificationChannel channel, string text, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (channel.Provider == NotificationProvider.Telegram && TelegramFailure is { } failure) throw failure;
            Sent.Add(channel.Provider); AfterSend?.Invoke(); return Task.FromResult(new NotificationAcceptance("fixture"));
        }
    }
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = BackupNotificationTests.Now; public override DateTimeOffset GetUtcNow() => Now; }
    [Fact]
    public async Task PersistedReceiptsPreventRepeatAfterRestartAndIgnoreOldAndRunningJobs()
    {
        await new NotificationSettingsStore(directory).SaveAsync(new() { Channels = [Channel()] });
        var history = new BackupRunHistory(Path.Combine(directory, "runs.json")); var failed = Run();
        await history.SaveAsync(failed); await history.SaveAsync(Run() with { FinishedUtc = Now.AddDays(-2), StartedUtc = Now.AddDays(-3) });
        await history.SaveAsync(Run() with { Status = BackupRunStatus.Running, FinishedUtc = null });
        var oldRun = Run();
        await history.SaveAsync(oldRun);
        var deliveryFile = Path.Combine(directory, "notification-deliveries.json");
        var channel = Assert.Single((await new NotificationSettingsStore(directory).LoadAsync()).Channels);
        await File.WriteAllTextAsync(deliveryFile, $$"""[{"ChannelId":"{{channel.Id}}","RunId":"{{oldRun.RunId}}"}]""");
        var sender = new Sender();
        Assert.Null(await new BackupNotificationDispatcher(directory, sender).PollAsync());
        Assert.Single(sender.Sent);
        Assert.Null(await new BackupNotificationDispatcher(directory, sender).PollAsync());
        Assert.Single(sender.Sent);
        Assert.DoesNotContain("secret", await File.ReadAllTextAsync(Path.Combine(directory, "notification-deliveries.json")));
    }
    [Fact]
    public async Task FailedChannelDoesNotBlockOtherChannelAndRetriesAfterBackoff()
    {
        await new NotificationSettingsStore(directory).SaveAsync(new() { Channels = [Channel(), Channel(NotificationProvider.Max)] });
        await new BackupRunHistory(Path.Combine(directory, "runs.json")).SaveAsync(Run());
        var sender = new Sender { TelegramFailure = new NotificationSendException(NotificationFailure.RateLimited, TimeSpan.FromMinutes(3)) }; var clock = new Clock();
        var dispatcher = new BackupNotificationDispatcher(directory, sender, clock);
        Assert.NotNull(await dispatcher.PollAsync()); Assert.Equal([NotificationProvider.Max], sender.Sent);
        File.Delete(Path.Combine(directory, "runs.json"));
        sender.TelegramFailure = null;
        dispatcher = new BackupNotificationDispatcher(directory, sender, clock);
        Assert.NotNull(await dispatcher.PollAsync()); Assert.Single(sender.Sent);
        clock.Now = Now.AddMinutes(1);
        Assert.NotNull(await dispatcher.PollAsync()); Assert.Single(sender.Sent);
        clock.Now = Now.AddMinutes(3);
        Assert.Null(await dispatcher.PollAsync()); Assert.Equal([NotificationProvider.Max, NotificationProvider.Telegram], sender.Sent);
        Assert.Null(await dispatcher.PollAsync()); Assert.Equal(2, sender.Sent.Count);
    }
    [Fact]
    public void EventSelectionAndSummaryKeepPrivateErrorsAndPathsLocal()
    {
        var settings = new NotificationSettings();
        Assert.True(settings.Includes(Run())); Assert.True(settings.Includes(Run(BackupRunStatus.Succeeded)));
        Assert.False(settings.Includes(Run(BackupRunStatus.Succeeded) with { Warnings = [] }));
        Assert.False(settings.Includes(Run(BackupRunStatus.Cancelled)));
        var run = Run();
        var summary = BotNotificationSender.Format(run);
        Assert.Contains("Тест <база>", summary); Assert.Contains("Ошибка", summary);
        Assert.Contains(run.RunId.ToString("N")[..8], summary);
        Assert.DoesNotContain("secret", summary); Assert.DoesNotContain("private", summary);
    }
    [Fact]
    public async Task ConcurrentProcessesUseSeparateNotificationLease()
    {
        await new NotificationSettingsStore(directory).SaveAsync(new() { Channels = [Channel()] });
        await new BackupRunHistory(Path.Combine(directory, "runs.json")).SaveAsync(Run());
        var sender = new Sender();
        using (new BackupEngineCoordinator(Path.Combine(directory, "notification-control")).Acquire())
            Assert.Null(await new BackupNotificationDispatcher(directory, sender).PollAsync());
        Assert.Empty(sender.Sent);
        using var backupLease = new BackupEngineCoordinator(directory).Acquire();
        Assert.Null(await new BackupNotificationDispatcher(directory, sender).PollAsync()); Assert.Single(sender.Sent);
    }
    [Fact]
    public async Task OversizedResponseIsRejectedWithoutReadingUnboundedContent()
    {
        using var client = new HttpClient(new Api((_, _) => Task.FromResult(Json(new string('x', 70000)))));
        await Assert.ThrowsAsync<NotificationSendException>(() => new BotNotificationSender(client, new Secrets("123:fixture")).SendAsync(Channel(), "test"));
    }
    [Fact]
    public async Task ReceiptWriteFailureIsWarningAndDoesNotChangeBackupOutcome()
    {
        await new NotificationSettingsStore(directory).SaveAsync(new() { Channels = [Channel()] });
        var run = Run(BackupRunStatus.Succeeded);
        var history = new BackupRunHistory(Path.Combine(directory, "runs.json")); await history.SaveAsync(run);
        // Keep the initial receipt read available; fail only the post-send write.
        // An unreadable existing receipt store must prevent sending duplicates.
        var sender = new Sender { AfterSend = () => Directory.CreateDirectory(Path.Combine(directory, "notification-deliveries.json")) };
        Assert.NotNull(await new BackupNotificationDispatcher(directory, sender).PollAsync()); Assert.Single(sender.Sent);
        Assert.Equal(BackupRunStatus.Succeeded, Assert.Single(await history.ReadAsync()).Status);
        sender.AfterSend = null;
        Assert.NotNull(await new BackupNotificationDispatcher(directory, sender).PollAsync());
        Assert.Single(sender.Sent);
        Directory.Delete(Path.Combine(directory, "notification-deliveries.json"));
        Assert.Null(await new BackupNotificationDispatcher(directory, sender).PollAsync());
        Assert.Single(sender.Sent);
        Assert.True(File.Exists(Path.Combine(directory, "notification-deliveries.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalResultReachesTheQueueBeforeFirstPoll(bool interrupted)
    {
        await new NotificationSettingsStore(directory).SaveAsync(new() { Channels = [Channel()] });
        var path = Path.Combine(directory, "runs.json");
        var history = new BackupRunHistory(path);
        var run = Run();
        await history.SaveAsync(interrupted ? run with { FinishedUtc = null, Status = BackupRunStatus.Running } : run);
        if (interrupted) await history.RecoverInterruptedAsync();
        File.Delete(path);
        var sender = new Sender();
        Assert.Null(await new BackupNotificationDispatcher(directory, sender).PollAsync());
        Assert.Single(sender.Sent);
        var json = await File.ReadAllTextAsync(Path.Combine(directory, "notification-queue.json"));
        Assert.Contains("Accepted", json);
        Assert.DoesNotContain("private", json);
        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain("-100123", json);
    }

    [Fact]
    public async Task HistoryRecoversDeliveryAfterQueueWriteFails()
    {
        await new NotificationSettingsStore(directory).SaveAsync(new() { Channels = [Channel()] });
        var queue = Path.Combine(directory, "notification-queue.json");
        Directory.CreateDirectory(queue);
        var history = new BackupRunHistory(Path.Combine(directory, "runs.json"));
        await history.SaveAsync(Run());
        Assert.Single(await history.ReadAsync());
        var sender = new Sender();
        Assert.NotNull(await new BackupNotificationDispatcher(directory, sender).PollAsync());
        Assert.Empty(sender.Sent);
        Directory.Delete(queue);
        Assert.Null(await new BackupNotificationDispatcher(directory, sender).PollAsync());
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task InterruptedAttemptWaitsBeforeRetryAfterRestart()
    {
        await new NotificationSettingsStore(directory).SaveAsync(new() { Channels = [Channel()] });
        await new BackupRunHistory(Path.Combine(directory, "runs.json")).SaveAsync(Run());
        using var cancellation = new CancellationTokenSource();
        var sender = new Sender { AfterSend = () => { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); } };
        var clock = new Clock();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new BackupNotificationDispatcher(directory, sender, clock).PollAsync(cancellation.Token));
        Assert.Single(sender.Sent);
        sender.AfterSend = null;
        var restarted = new BackupNotificationDispatcher(directory, sender, clock);
        Assert.Contains("неизвестен", await restarted.PollAsync());
        Assert.Single(sender.Sent);
        clock.Now = Now.AddMinutes(1);
        Assert.Null(await restarted.PollAsync());
        Assert.Equal(2, sender.Sent.Count);
    }

    [Fact]
    public async Task PendingDeliveryDoesNotFollowChangedRecipient()
    {
        var channel = Channel();
        var store = new NotificationSettingsStore(directory);
        await store.SaveAsync(new() { Channels = [channel] });
        var history = new BackupRunHistory(Path.Combine(directory, "runs.json"));
        await history.SaveAsync(Run());
        await store.SaveAsync(new() { Channels = [channel with { Recipient = "-100456" }] });
        var sender = new Sender();
        Assert.Null(await new BackupNotificationDispatcher(directory, sender).PollAsync());
        Assert.Empty(sender.Sent);
        Assert.Contains("Cancelled", await File.ReadAllTextAsync(Path.Combine(directory, "notification-queue.json")));
    }

    [Fact]
    public async Task BlockedRouteWaitsForNewCredentialsAndOtherProviderContinues()
    {
        var telegram = Channel();
        var max = Channel(NotificationProvider.Max);
        var store = new NotificationSettingsStore(directory);
        await store.SaveAsync(new() { Channels = [telegram, max] });
        await new BackupRunHistory(Path.Combine(directory, "runs.json")).SaveAsync(Run());
        var clock = new Clock();
        var sender = new Sender { TelegramFailure = new NotificationSendException(NotificationFailure.Authorization) };
        Assert.NotNull(await new BackupNotificationDispatcher(directory, sender, clock).PollAsync());
        Assert.Equal([NotificationProvider.Max], sender.Sent);
        clock.Now += TimeSpan.FromHours(1);
        sender.TelegramFailure = null;
        Assert.NotNull(await new BackupNotificationDispatcher(directory, sender, clock).PollAsync());
        Assert.Equal([NotificationProvider.Max], sender.Sent);
        await store.SaveAsync(new() { Channels = [telegram with { CredentialKey = "updated" }, max] });
        Assert.Null(await new BackupNotificationDispatcher(directory, sender, clock).PollAsync());
        Assert.Equal([NotificationProvider.Max, NotificationProvider.Telegram], sender.Sent);
    }

    private sealed class WaitingSender : IBackupNotificationSender
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Accepted;

        public async Task<NotificationAcceptance> SendAsync(NotificationChannel channel, string text, CancellationToken token = default)
        {
            Started.TrySetResult();
            await Continue.Task.WaitAsync(token);
            return new NotificationAcceptance(Interlocked.Increment(ref Accepted).ToString());
        }
    }

    [Fact]
    public async Task SlowMessengerDoesNotHoldQueueWriteLock()
    {
        await new NotificationSettingsStore(directory).SaveAsync(new() { Channels = [Channel()] });
        var path = Path.Combine(directory, "runs.json");
        var history = new BackupRunHistory(path);
        await history.SaveAsync(Run());
        var sender = new WaitingSender();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var poll = new BackupNotificationDispatcher(directory, sender).PollAsync(timeout.Token);
        try
        {
            await sender.Started.Task.WaitAsync(timeout.Token);
            await history.SaveAsync(Run(), timeout.Token);
            File.Delete(path);
            Assert.False(poll.IsCompleted);
        }
        finally { sender.Continue.TrySetResult(); }
        Assert.Null(await poll);
        Assert.Equal(2, sender.Accepted);
    }

    [Fact]
    public async Task QueueLimitLeavesExcessRunsAvailableForLaterCapture()
    {
        var channel = Channel();
        var settings = new NotificationSettings { Channels = [channel] };
        var queue = new BackupNotificationQueue(directory);
        var runs = Enumerable.Range(0, BackupNotificationQueue.PendingLimit + 1).Select(_ => Run()).ToArray();
        Assert.Contains("заполнена", await queue.PrepareAsync(settings, runs, Now, CancellationToken.None));
        var first = await queue.ClaimAsync(channel, Now, CancellationToken.None);
        Assert.NotNull(first.Item);
        Assert.Null(await queue.CompleteAsync(channel, first.Item, new("fixture"), null, Now));
        Assert.Null(await queue.PrepareAsync(settings, runs, Now, CancellationToken.None));
    }

    [Fact]
    public async Task ExpiredDeliveryIsNotSent()
    {
        await new NotificationSettingsStore(directory).SaveAsync(new() { Channels = [Channel()] });
        await new BackupRunHistory(Path.Combine(directory, "runs.json")).SaveAsync(Run());
        var sender = new Sender();
        var clock = new Clock { Now = Now + BackupNotificationQueue.Lifetime };
        Assert.Null(await new BackupNotificationDispatcher(directory, sender, clock).PollAsync());
        Assert.Empty(sender.Sent);
        Assert.Contains("Expired", await File.ReadAllTextAsync(Path.Combine(directory, "notification-queue.json")));
    }
}
