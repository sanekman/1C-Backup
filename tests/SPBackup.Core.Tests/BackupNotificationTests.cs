using System.Net;
using System.Text;
using System.Text.Json;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class BackupNotificationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OneCBackup-notifications-" + Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
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
        await new BotNotificationSender(client, new Secrets(secret)).SendAsync(channel, "Тест <база>");
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
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new BotNotificationSender(client, new Secrets("123:fixture")).SendAsync(Channel(provider), "test"));
        Assert.DoesNotContain("TOKEN_private", error.Message); Assert.DoesNotContain("123:fixture", error.Message);
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
        using var client = new HttpClient(new Api(async (_, token) => { cancellation.Cancel(); await Task.Delay(Timeout.Infinite, token); return Json("{}"); }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BotNotificationSender(client, new Secrets("123:fixture")).SendAsync(Channel(), "test", cancellation.Token));
    }
    private sealed class Sender : IBackupNotificationSender
    {
        public List<NotificationProvider> Sent { get; } = [];
        public bool DenyTelegram;
        public Action? AfterSend;
        public Task SendAsync(NotificationChannel channel, string text, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (DenyTelegram && channel.Provider == NotificationProvider.Telegram) throw new HttpRequestException("fixture offline");
            Sent.Add(channel.Provider); AfterSend?.Invoke(); return Task.CompletedTask;
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
        var sender = new Sender { DenyTelegram = true }; var clock = new Clock();
        var dispatcher = new BackupNotificationDispatcher(directory, sender, clock);
        Assert.NotNull(await dispatcher.PollAsync()); Assert.Equal([NotificationProvider.Max], sender.Sent);
        sender.DenyTelegram = false;
        Assert.NotNull(await dispatcher.PollAsync()); Assert.Single(sender.Sent);
        clock.Now = Now.AddMinutes(1);
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
        var summary = BotNotificationSender.Format(Run());
        Assert.Contains("Тест <база>", summary); Assert.Contains("Ошибка", summary);
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => new BotNotificationSender(client, new Secrets("123:fixture")).SendAsync(Channel(), "test"));
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
    }
}
