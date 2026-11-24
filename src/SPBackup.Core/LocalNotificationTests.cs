using System.Security.Cryptography;
using System.Text.Json;

namespace SPBackup.Core;

public sealed record LocalNotificationTestRequest(Guid Id, Guid SessionId, Guid ChannelId, string Configuration, DateTimeOffset SubmittedUtc);
public enum LocalNotificationTestOutcome { Accepted, Failed, ChannelChanged }
public sealed record LocalNotificationTestResult(Guid Id, Guid SessionId, int ProcessId, LocalNotificationTestOutcome Outcome,
    NotificationFailure? Failure = null);

public sealed class LocalNotificationTests(string directory)
{
    private static readonly TimeSpan RequestLifetime = TimeSpan.FromSeconds(30);
    internal const string TestMessage = "SP Backup: тестовое оповещение. Подключение настроено.";
    private readonly string requestPath = Path.Combine(directory, "notification-test-request.json");
    private readonly AgentJsonFile<LocalNotificationTestResult> resultFile = new(Path.Combine(directory, "notification-test-result.json"));

    public static bool IsAvailable(LocalAgentState? state, DateTimeOffset now) => state is
        { Phase: LocalAgentPhase.Running, CanTestNotifications: true, SessionId: var id, UpdatedUtc: var updated }
        && id != Guid.Empty && updated <= now && now - updated < TimeSpan.FromSeconds(10);

    public async Task<LocalNotificationTestResult> TestAsync(NotificationProvider provider, CancellationToken token = default)
    {
        var control = new LocalAgentControlStore(directory);
        var state = await control.ReadAsync(token);
        if (!IsAvailable(state, DateTimeOffset.UtcNow))
            throw new InvalidOperationException("Запустите фоновый агент или службу с поддержкой теста оповещений.");
        var settings = await new NotificationSettingsStore(directory).LoadAsync(token);
        var channel = settings.Channels.SingleOrDefault(c => c.Provider == provider && c.Enabled)
            ?? throw new InvalidOperationException("Включите канал и сохраните его настройки перед тестом через агент.");
        var request = new LocalNotificationTestRequest(Guid.NewGuid(), state!.SessionId, channel.Id, Fingerprint(channel), DateTimeOffset.UtcNow);
        using (Acquire())
        {
            var file = new AgentJsonFile<LocalNotificationTestRequest>(requestPath);
            var pending = await file.ReadBoundedAsync(8192, token);
            if (pending?.SessionId == request.SessionId && request.SubmittedUtc - pending.SubmittedUtc < RequestLifetime)
                throw new InvalidOperationException("Тест уже ожидает выполнения фоновым агентом.");
            await file.WriteAsync(request, token);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(RequestLifetime + TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                var result = await resultFile.ReadBoundedAsync(8192, deadline.Token);
                if (result?.Id == request.Id && result.SessionId == request.SessionId && result.ProcessId == state.ProcessId
                    && Enum.IsDefined(result.Outcome) && (result.Failure is null || Enum.IsDefined(result.Failure.Value)))
                    return result;
                var current = await control.ReadAsync(deadline.Token);
                if (current?.SessionId != request.SessionId || !IsAvailable(current, DateTimeOffset.UtcNow))
                    throw new InvalidOperationException("Фоновый агент остановлен или перезапущен. Результат теста неизвестен.");
                await Task.Delay(300, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new InvalidOperationException("Фоновый агент не подтвердил тест вовремя. Проверьте чат перед повтором."); }
    }

    internal async Task ProcessAsync(Guid sessionId, IBackupNotificationSender sender, CancellationToken token)
    {
        if (!File.Exists(requestPath)) return;
        LocalNotificationTestRequest? request;
        try
        {
            using var lease = Acquire();
            request = await new AgentJsonFile<LocalNotificationTestRequest>(requestPath).ReadBoundedAsync(8192, token);
            if (request is null) return;
            // Consume before any network action so a lost receipt cannot replay the request.
            File.Delete(requestPath);
        }
        catch (BackupEngineBusyException) { return; }
        var age = DateTimeOffset.UtcNow - request.SubmittedUtc;
        if (request.Id == Guid.Empty || request.SessionId != sessionId || age < TimeSpan.Zero || age >= RequestLifetime) return;
        var settings = await new NotificationSettingsStore(directory).LoadAsync(token);
        var channel = settings.Channels.SingleOrDefault(c => c.Enabled && c.Id == request.ChannelId && Fingerprint(c) == request.Configuration);
        var result = new LocalNotificationTestResult(request.Id, sessionId, Environment.ProcessId, LocalNotificationTestOutcome.ChannelChanged);
        if (channel is not null)
        {
            try
            {
                await sender.SendAsync(channel, TestMessage, token);
                result = result with { Outcome = LocalNotificationTestOutcome.Accepted };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (NotificationSendException error)
            { result = result with { Outcome = LocalNotificationTestOutcome.Failed, Failure = error.Failure }; }
            catch (InvalidDataException)
            { result = result with { Outcome = LocalNotificationTestOutcome.Failed, Failure = NotificationFailure.InvalidToken }; }
            catch (Exception)
            { result = result with { Outcome = LocalNotificationTestOutcome.Failed, Failure = NotificationFailure.OutcomeUnknown }; }
        }
        await resultFile.WriteAsync(result, CancellationToken.None);
    }

    private IDisposable Acquire() => new BackupEngineCoordinator(Path.Combine(directory, "notification-test-control")).Acquire();
    private static string Fingerprint(NotificationChannel channel) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(channel)));
}
