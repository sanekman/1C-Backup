using System.Text.Json;

namespace SPBackup.Core;

public enum LocalSourceCheckPhase { Accepted, Completed, Cancelled, Interrupted, Rejected }
public sealed record LocalSourceCheckRequest(Guid RequestId, Guid SessionId, Guid JobId, string JobFingerprint, DateTimeOffset SubmittedUtc)
{
    // Standalone loaders normalize the time zone; it does not affect a source availability probe.
    public static string Fingerprint(BackupJob job) => LocalStorageCheckRequest.Fingerprint(job);
}
public sealed record LocalSourceCheckReceipt(Guid RequestId, Guid SessionId, Guid JobId, string JobFingerprint,
    LocalSourceCheckPhase Phase, SourceCheckReport? Report = null, string? Error = null);
internal sealed record LocalSourceCheckCancel(Guid RequestId, Guid SessionId);

/// <summary>Source-only channel. Older agents ignore it; it never falls back to a backup or a window probe.</summary>
public sealed class LocalSourceCheckQueue(string directory)
{
    public static bool IsAvailable(LocalAgentState? state, DateTimeOffset now) => state is
        { Phase: LocalAgentPhase.Running, CanCheckSources: true, ProcessId: > 0 }
        && state.SessionId != Guid.Empty && state.StartedUtc != default && state.UpdatedUtc >= state.StartedUtc
        && state.UpdatedUtc <= now && now - state.UpdatedUtc < TimeSpan.FromSeconds(10);
    private string Root => Path.Combine(directory, "source-checks");
    private string Pending => Path.Combine(Root, "pending");
    private string FilePath(string kind, Guid id) => Path.Combine(Root, kind, id.ToString("N") + ".json");
    public async Task<LocalSourceCheckRequest> EnqueueAsync(Guid sessionId, BackupJob job, CancellationToken token = default)
    {
        if (sessionId == Guid.Empty || job.Id == Guid.Empty) throw new ArgumentException("Не указан сеанс агента или задание.");
        Directory.CreateDirectory(Pending);
        if (Directory.EnumerateFiles(Pending, "*.json").Take(32).Count() >= 32) throw new IOException("Очередь проверок заполнена. Дождитесь завершения предыдущих проверок.");
        var request = new LocalSourceCheckRequest(Guid.NewGuid(), sessionId, job.Id, LocalSourceCheckRequest.Fingerprint(job), DateTimeOffset.UtcNow);
        await new AgentJsonFile<LocalSourceCheckRequest>(FilePath("pending", request.RequestId)).WriteAsync(request, token);
        return request;
    }
    internal async Task<IReadOnlyList<LocalSourceCheckRequest>> PendingAsync(CancellationToken token)
    {
        if (!Directory.Exists(Pending)) return [];
        var result = new List<LocalSourceCheckRequest>();
        foreach (var path in Directory.EnumerateFiles(Pending, "*.json").Take(64))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id)) continue;
                var r = await new AgentJsonFile<LocalSourceCheckRequest>(path).ReadBoundedAsync(8192, token);
                if (r is not null && r.RequestId == id && r.SessionId != Guid.Empty && r.JobId != Guid.Empty
                    && r.SubmittedUtc != default && r.JobFingerprint is { Length: 64 } && r.JobFingerprint.All(Uri.IsHexDigit)) result.Add(r);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { }
        }
        return result.OrderBy(r => r.SubmittedUtc).ToArray();
    }
    public async Task<LocalSourceCheckReceipt?> ReadAsync(LocalSourceCheckRequest request, CancellationToken token = default)
    {
        var path = FilePath("receipts", request.RequestId);
        var value = await new AgentJsonFile<LocalSourceCheckReceipt>(path).ReadBoundedAsync(2 * 1024 * 1024, token);
        if (value is not null && (value.RequestId != request.RequestId || value.SessionId != request.SessionId
            || value.JobId != request.JobId || value.JobFingerprint != request.JobFingerprint || !Enum.IsDefined(value.Phase)
            || value.Phase == LocalSourceCheckPhase.Completed && value.Report is null))
            throw new InvalidDataException("Результат принадлежит другой проверке.");
        if (value?.Report is { } report && (string.IsNullOrWhiteSpace(report.Details) || report.Details.Length > 1024 * 1024
            || report.ExecutionIdentity is null || string.IsNullOrWhiteSpace(report.ExecutionIdentity.Account)
            || report.ExecutionIdentity.Account.Length > 512 || report.StartedUtc is null || report.FinishedUtc < report.StartedUtc
            || report.FinishedUtc is null || report.Cancelled && report.Succeeded
            || value.Phase is LocalSourceCheckPhase.Accepted or LocalSourceCheckPhase.Rejected
            || value.Phase == LocalSourceCheckPhase.Completed && report.Cancelled
            || value.Phase is LocalSourceCheckPhase.Cancelled or LocalSourceCheckPhase.Interrupted && !report.Cancelled))
            throw new InvalidDataException("Исполнитель вернул неполный результат проверки источника или не указал пользователя и время.");
        return value;
    }
    public Task CancelAsync(LocalSourceCheckRequest request, CancellationToken token = default) =>
        new AgentJsonFile<LocalSourceCheckCancel>(FilePath("cancel", request.RequestId)).WriteAsync(new(request.RequestId, request.SessionId), token);
    internal async Task<bool> IsCancelledAsync(LocalSourceCheckRequest request, CancellationToken token)
    {
        var path = FilePath("cancel", request.RequestId);
        var value = await new AgentJsonFile<LocalSourceCheckCancel>(path).ReadBoundedAsync(8192, token);
        return value?.RequestId == request.RequestId && value.SessionId == request.SessionId;
    }
    internal Task WriteAsync(LocalSourceCheckReceipt receipt, CancellationToken token) =>
        new AgentJsonFile<LocalSourceCheckReceipt>(FilePath("receipts", receipt.RequestId)).WriteAsync(receipt, token);
    internal void Consume(LocalSourceCheckRequest request) => File.Delete(FilePath("pending", request.RequestId));

    public async Task<SourceCheckReport> WaitAsync(LocalSourceCheckRequest request, CancellationToken token = default)
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
                if (receipt is { Phase: not LocalSourceCheckPhase.Accepted })
                {
                    finished = true;
                    if (receipt.Report is { } report) return report;
                    if (receipt.Phase == LocalSourceCheckPhase.Cancelled) throw new IOException(receipt.Error ?? "Исполнитель отменил проверку. Повторите её при необходимости.");
                    throw new IOException(receipt.Error ?? "Проверка не завершена. Повторите её после запуска агента.");
                }
                var state = await control.ReadAsync(deadline.Token);
                if (state?.SessionId != request.SessionId || state.Phase != LocalAgentPhase.Running
                    || !state.CanCheckSources || state.UpdatedUtc > DateTimeOffset.UtcNow.AddSeconds(5)
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

/// <summary>Reads source availability under the host identity, without invoking a backup executor or changing history.</summary>
internal sealed class LocalSourceCheckProcessor(string directory, IBackupJobSource jobs, SourceReadinessChecker checker)
{
    private static bool Recoverable(Exception error) => AgentRuntimeWarnings.Recoverable(error)
        || error is InvalidDataException or JsonException or ArgumentException or NotSupportedException;
    internal async Task RunAsync(Guid sessionId, Action<Exception?> warning, CancellationToken token)
    {
        var queue = new LocalSourceCheckQueue(directory);
        var results = new Dictionary<Guid, LocalSourceCheckReceipt>();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        async Task Save(LocalSourceCheckReceipt receipt)
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
        async Task Handle(LocalSourceCheckRequest request)
        {
            token.ThrowIfCancellationRequested();
            var basis = new LocalSourceCheckReceipt(request.RequestId, request.SessionId, request.JobId,
                request.JobFingerprint, LocalSourceCheckPhase.Rejected);
            LocalSourceCheckReceipt? existing;
            try { existing = await queue.ReadAsync(request, token); }
            catch (Exception error) when (error is JsonException or InvalidDataException)
            {
                await Save(basis with { Error = "Предыдущее подтверждение повреждено. Отправьте новую проверку." });
                queue.Consume(request); return;
            }
            if (existing is { Phase: not LocalSourceCheckPhase.Accepted }) { queue.Consume(request); return; }
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
                await Save(basis with { Phase = LocalSourceCheckPhase.Cancelled, Error = "Проверка отменена до обращения к источнику." });
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
                : LocalSourceCheckRequest.Fingerprint(job) != request.JobFingerprint ? "Настройки задания изменились. Обновите список заданий и повторите проверку." : null;
            if (rejection is not null)
            {
                await Save(basis with { Error = rejection }); queue.Consume(request); return;
            }
            if (await queue.IsCancelledAsync(request, token))
            {
                await Save(basis with { Phase = LocalSourceCheckPhase.Cancelled, Error = "Проверка отменена до обращения к источнику." });
                queue.Consume(request); return;
            }
            await queue.WriteAsync(basis with { Phase = LocalSourceCheckPhase.Accepted }, token);
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
            LocalSourceCheckReceipt completed;
            try
            {
                if (await queue.IsCancelledAsync(request, token)) stop.Cancel();
                var report = await Task.Run(() => checker.CheckAsync(job!, stop.Token), CancellationToken.None);
                completed = basis with { Phase = report.Cancelled
                    ? token.IsCancellationRequested ? LocalSourceCheckPhase.Interrupted : LocalSourceCheckPhase.Cancelled
                    : LocalSourceCheckPhase.Completed, Report = report };
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            { completed = basis with { Phase = token.IsCancellationRequested ? LocalSourceCheckPhase.Interrupted : LocalSourceCheckPhase.Cancelled, Error = "Проверка отменена или исполнитель остановлен." }; }
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
