using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace SPBackup.Core;

public sealed record StorageCheckIdentity(string Account, string? WindowsSid)
{
    public static StorageCheckIdentity Current()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            string account;
            try { account = identity.Name; }
            catch (IdentityNotMappedException) { account = identity.User?.Value ?? Environment.UserName; }
            return new(account, identity.User?.Value);
        }
        return new(Environment.UserName, null);
    }
}

public enum LocalStorageCheckPhase { Accepted, Completed, Cancelled, Interrupted, Rejected }
public sealed record LocalStorageCheckRequest(Guid RequestId, Guid SessionId, Guid JobId, string JobFingerprint, DateTimeOffset SubmittedUtc)
{
    // Standalone loaders normalize the time zone; it does not affect a storage access probe.
    public static string Fingerprint(BackupJob job) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(job with { TimeZoneId = BackupSchedule.LocalTimeZone })));
}
public sealed record LocalStorageCheckReceipt(Guid RequestId, Guid SessionId, Guid JobId, string JobFingerprint,
    LocalStorageCheckPhase Phase, StorageCheckReport? Report = null, string? Error = null);
internal sealed record LocalStorageCheckCancel(Guid RequestId, Guid SessionId);

/// <summary>Separate from backup commands: older agents ignore this channel instead of executing a backup.</summary>
public sealed class LocalStorageCheckQueue(string directory)
{
    public static bool IsAvailable(LocalAgentState? state, DateTimeOffset now) => state is
        { Phase: LocalAgentPhase.Running, CanCheckStorages: true, ProcessId: > 0 }
        && state.SessionId != Guid.Empty && state.StartedUtc != default && state.UpdatedUtc >= state.StartedUtc
        && state.UpdatedUtc <= now && now - state.UpdatedUtc < TimeSpan.FromSeconds(10);
    private string Root => Path.Combine(directory, "storage-checks");
    private string Pending => Path.Combine(Root, "pending");
    private string FilePath(string kind, Guid id) => Path.Combine(Root, kind, id.ToString("N") + ".json");
    public async Task<LocalStorageCheckRequest> EnqueueAsync(Guid sessionId, BackupJob job, CancellationToken token = default)
    {
        if (sessionId == Guid.Empty || job.Id == Guid.Empty) throw new ArgumentException("Не указан сеанс агента или задание.");
        Directory.CreateDirectory(Pending);
        if (Directory.EnumerateFiles(Pending, "*.json").Take(32).Count() >= 32) throw new IOException("Очередь проверок заполнена. Дождитесь завершения предыдущих проверок.");
        var request = new LocalStorageCheckRequest(Guid.NewGuid(), sessionId, job.Id, LocalStorageCheckRequest.Fingerprint(job), DateTimeOffset.UtcNow);
        await new AgentJsonFile<LocalStorageCheckRequest>(FilePath("pending", request.RequestId)).WriteAsync(request, token);
        return request;
    }
    internal async Task<IReadOnlyList<LocalStorageCheckRequest>> PendingAsync(CancellationToken token)
    {
        if (!Directory.Exists(Pending)) return [];
        var result = new List<LocalStorageCheckRequest>();
        foreach (var path in Directory.EnumerateFiles(Pending, "*.json").Take(64))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id)) continue;
                var r = await new AgentJsonFile<LocalStorageCheckRequest>(path).ReadBoundedAsync(8192, token);
                if (r is not null && r.RequestId == id && r.SessionId != Guid.Empty && r.JobId != Guid.Empty
                    && r.SubmittedUtc != default && r.JobFingerprint is { Length: 64 } && r.JobFingerprint.All(Uri.IsHexDigit)) result.Add(r);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { }
        }
        return result.OrderBy(r => r.SubmittedUtc).ToArray();
    }
    public async Task<LocalStorageCheckReceipt?> ReadAsync(LocalStorageCheckRequest request, CancellationToken token = default)
    {
        var path = FilePath("receipts", request.RequestId);
        var value = await new AgentJsonFile<LocalStorageCheckReceipt>(path).ReadBoundedAsync(2 * 1024 * 1024, token);
        if (value is not null && (value.RequestId != request.RequestId || value.SessionId != request.SessionId
            || value.JobId != request.JobId || value.JobFingerprint != request.JobFingerprint || !Enum.IsDefined(value.Phase)
            || value.Phase == LocalStorageCheckPhase.Completed && value.Report is null))
            throw new InvalidDataException("Результат принадлежит другой проверке.");
        if (value?.Report is { } report && (string.IsNullOrWhiteSpace(report.TemporaryDirectory)
            || report.ExecutionIdentity is null || string.IsNullOrWhiteSpace(report.ExecutionIdentity.Account)
            || report.ExecutionIdentity.Account.Length > 512 || report.Destinations is null || report.Destinations.Count > 64
            || value.Phase is LocalStorageCheckPhase.Accepted or LocalStorageCheckPhase.Rejected
            || value.Phase == LocalStorageCheckPhase.Completed && report.Cancelled
            || value.Phase is LocalStorageCheckPhase.Cancelled or LocalStorageCheckPhase.Interrupted && !report.Cancelled
            || report.Destinations.Any(item => item is null || string.IsNullOrWhiteSpace(item.Destination)
                || item.Succeeded && item.Error is not null)))
            throw new InvalidDataException("Исполнитель вернул неполный результат проверки или не указал пользователя.");
        return value;
    }
    public Task CancelAsync(LocalStorageCheckRequest request, CancellationToken token = default) =>
        new AgentJsonFile<LocalStorageCheckCancel>(FilePath("cancel", request.RequestId)).WriteAsync(new(request.RequestId, request.SessionId), token);
    internal async Task<bool> IsCancelledAsync(LocalStorageCheckRequest request, CancellationToken token)
    {
        var path = FilePath("cancel", request.RequestId);
        var value = await new AgentJsonFile<LocalStorageCheckCancel>(path).ReadBoundedAsync(8192, token);
        return value?.RequestId == request.RequestId && value.SessionId == request.SessionId;
    }
    internal Task WriteAsync(LocalStorageCheckReceipt receipt, CancellationToken token) =>
        new AgentJsonFile<LocalStorageCheckReceipt>(FilePath("receipts", receipt.RequestId)).WriteAsync(receipt, token);
    internal void Consume(LocalStorageCheckRequest request) => File.Delete(FilePath("pending", request.RequestId));

    public async Task<StorageCheckReport> WaitAsync(LocalStorageCheckRequest request, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(4));
        var control = new LocalAgentControlStore(directory);
        var finished = false;
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var receipt = await ReadAsync(request, deadline.Token);
                if (receipt is { Phase: not LocalStorageCheckPhase.Accepted })
                {
                    finished = true;
                    if (receipt.Report is { } report) return report;
                    if (receipt.Phase == LocalStorageCheckPhase.Cancelled) throw new IOException(receipt.Error ?? "Исполнитель отменил проверку. Повторите её при необходимости.");
                    throw new IOException(receipt.Error ?? "Проверка не завершена. Повторите её после запуска агента.");
                }
                var state = await control.ReadAsync(deadline.Token);
                if (state?.SessionId != request.SessionId || state.Phase != LocalAgentPhase.Running
                    || !state.CanCheckStorages || state.UpdatedUtc > DateTimeOffset.UtcNow.AddSeconds(5)
                    || DateTimeOffset.UtcNow - state.UpdatedUtc > TimeSpan.FromSeconds(15))
                    throw new IOException("Исполнитель остановлен или потерял связь. Результат проверки не подтверждён. Запустите агент и повторите проверку.");
                await Task.Delay(250, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException("Время ожидания проверки истекло. Исполнитель может ещё завершать проверку; дождитесь восстановления связи перед повтором."); }
        finally
        {
            if (!finished)
            {
                try { await CancelAsync(request, CancellationToken.None); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { DiagnosticLog.Current.Write(DiagnosticEvent.AgentWarning, DiagnosticLevel.Warning, error); }
            }
        }
    }
}

/// <summary>Runs only disposable storage probes. Never invokes an executor or changes backup history/catalog.</summary>
internal sealed class LocalStorageCheckProcessor(string directory, IBackupJobSource jobs, StorageReadinessChecker checker)
{
    private static bool Recoverable(Exception error) => AgentRuntimeWarnings.Recoverable(error)
        || error is InvalidDataException or JsonException or ArgumentException or NotSupportedException;
    internal async Task RunAsync(Guid sessionId, Action<Exception?> warning, CancellationToken token)
    {
        var queue = new LocalStorageCheckQueue(directory);
        var results = new Dictionary<Guid, LocalStorageCheckReceipt>();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        async Task Save(LocalStorageCheckReceipt receipt)
        {
            results[receipt.RequestId] = receipt;
            await queue.WriteAsync(receipt, CancellationToken.None);
            results.Remove(receipt.RequestId);
        }
        var failures = new Dictionary<Guid, (DateTimeOffset RetryAfter, Exception Error)>();
        async Task TryCommand(Guid id, Func<Task> operation)
        {
            if (failures.TryGetValue(id, out var previous) && previous.RetryAfter > DateTimeOffset.UtcNow) return;
            try { await operation(); failures.Remove(id); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (Recoverable(error)) { failures[id] = (DateTimeOffset.UtcNow.AddSeconds(1), error); }
        }
        async Task Handle(LocalStorageCheckRequest request)
        {
            token.ThrowIfCancellationRequested();
            var basis = new LocalStorageCheckReceipt(request.RequestId, request.SessionId, request.JobId,
                request.JobFingerprint, LocalStorageCheckPhase.Rejected);
            LocalStorageCheckReceipt? existing;
            try { existing = await queue.ReadAsync(request, token); }
            catch (Exception error) when (error is JsonException or InvalidDataException)
            {
                await Save(basis with { Error = "Предыдущее подтверждение повреждено. Отправьте новую проверку." });
                queue.Consume(request); return;
            }
            if (existing is { Phase: not LocalStorageCheckPhase.Accepted }) { queue.Consume(request); return; }
            string? rejection = existing is not null ? "Эта проверка уже была принята. Отправьте новую команду."
                : request.SessionId != sessionId ? "Сеанс исполнителя завершён. Повторите проверку через работающий агент."
                : DateTimeOffset.UtcNow - request.SubmittedUtc > TimeSpan.FromMinutes(3) || request.SubmittedUtc > DateTimeOffset.UtcNow.AddMinutes(1)
                    ? "Команда проверки устарела. Повторите проверку." : null;
            bool cancelled = false;
            if (rejection is null)
            {
                try { cancelled = await queue.IsCancelledAsync(request, token); }
                catch (Exception error) when (error is JsonException or InvalidDataException)
                { rejection = "Команда отмены проверки повреждена. Отправьте новую проверку."; }
            }
            if (cancelled)
            {
                await Save(basis with { Phase = LocalStorageCheckPhase.Cancelled, Error = "Проверка отменена до обращения к хранилищам." });
                queue.Consume(request); return;
            }
            BackupJob? job = null;
            if (rejection is null)
            {
                try { job = (await jobs.LoadAsync(token)).FirstOrDefault(j => j.Id == request.JobId); }
                catch (Exception error) when (Recoverable(error))
                { warning(error); rejection = "Не удалось прочитать сохранённые задания исполнителя. Проверьте диагностику и сохраните настройки заново."; }
            }
            rejection ??= job is null ? "Сохранённое задание не найдено."
                : LocalStorageCheckRequest.Fingerprint(job) != request.JobFingerprint ? "Настройки задания изменились. Обновите список заданий и повторите проверку." : null;
            if (rejection is not null)
            {
                await Save(basis with { Error = rejection }); queue.Consume(request); return;
            }
            if (await queue.IsCancelledAsync(request, token))
            {
                await Save(basis with { Phase = LocalStorageCheckPhase.Cancelled, Error = "Проверка отменена до обращения к хранилищам." });
                queue.Consume(request); return;
            }
            await queue.WriteAsync(basis with { Phase = LocalStorageCheckPhase.Accepted }, token);
            queue.Consume(request);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            stop.CancelAfter(TimeSpan.FromMinutes(3));
            using var stopWatching = CancellationTokenSource.CreateLinkedTokenSource(token);
            Exception? watchError = null;
            async Task Watch()
            {
                try
                {
                    while (true)
                    {
                        if (await queue.IsCancelledAsync(request, stopWatching.Token)) { stop.Cancel(); return; }
                        await Task.Delay(100, stopWatching.Token);
                    }
                }
                catch (OperationCanceledException) when (stopWatching.IsCancellationRequested) { }
                catch (Exception error) when (Recoverable(error)) { watchError = error; stop.Cancel(); }
            }
            var watch = Watch();
            LocalStorageCheckReceipt completed;
            try
            {
                if (await queue.IsCancelledAsync(request, token)) stop.Cancel();
                var report = await Task.Run(() => checker.CheckAsync(job!, stop.Token), CancellationToken.None);
                completed = basis with { Phase = report.Cancelled
                    ? token.IsCancellationRequested ? LocalStorageCheckPhase.Interrupted : LocalStorageCheckPhase.Cancelled
                    : LocalStorageCheckPhase.Completed, Report = report };
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            { completed = basis with { Phase = token.IsCancellationRequested ? LocalStorageCheckPhase.Interrupted : LocalStorageCheckPhase.Cancelled, Error = "Проверка отменена или исполнитель остановлен." }; }
            catch (Exception error) when (Recoverable(error))
            { completed = basis with { Error = "Проверка не завершена: " + error.Message }; }
            finally
            {
                stopWatching.Cancel();
                try { await watch; }
                catch (OperationCanceledException) when (stopWatching.IsCancellationRequested) { }
            }
            if (watchError is not null) completed = basis with { Error = "Не удалось прочитать команду отмены проверки: " + watchError.Message };
            await Save(completed);
        }
        do
        {
            try
            {
                foreach (var result in results.Values.ToArray()) await TryCommand(result.RequestId, () => Save(result));
                var pending = await queue.PendingAsync(token);
                var active = pending.Select(request => request.RequestId).Concat(results.Keys).ToHashSet();
                foreach (var removed in failures.Keys.Where(id => !active.Contains(id)).ToArray()) failures.Remove(removed);
                foreach (var request in pending) await TryCommand(request.RequestId, () => Handle(request));
                warning(failures.Values.Select(value => value.Error).FirstOrDefault());
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (Recoverable(error)) { warning(error); }
        } while (await timer.WaitForNextTickAsync(token));
    }
}
