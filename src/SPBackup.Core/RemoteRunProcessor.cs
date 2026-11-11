using System.Net;

namespace SPBackup.Core;

public interface IRemoteRunApi
{
    Task<IReadOnlyList<RemoteRunCommand>> PollCommandsAsync(AgentSettings settings, CancellationToken token);
    Task SendCommandReceiptAsync(AgentSettings settings, RemoteRunReceipt receipt, CancellationToken token);
}
public sealed record RemoteCommandJournal(RemoteRunCommand Command, RemoteRunReceipt Receipt, bool Delivered = false);

/// <summary>One processor under the host lease. A durable start marker is never replayed after an uncertain outcome.</summary>
public sealed class RemoteRunProcessor(string directory, AgentSettings settings, IRemoteRunApi api,
    AgentExecutionJobSource source, LocalBackupDispatcher dispatcher, Func<BackupRun?> readRun, TimeProvider? clock = null)
{
    public bool JournalWarning { get; private set; }
    private string JournalPath(Guid id) => Path.Combine(directory, id.ToString("N") + ".json");
    private Task WriteAsync(RemoteCommandJournal value, CancellationToken token) =>
        new AgentJsonFile<RemoteCommandJournal>(JournalPath(value.Command.CommandId)).WriteAsync(value, token);
    private async Task<RemoteCommandJournal?> ReadAsync(Guid id, CancellationToken token)
    {
        var value = await new AgentJsonFile<RemoteCommandJournal>(JournalPath(id)).ReadBoundedAsync(8192, token);
        if (value is null && File.Exists(JournalPath(id)) || value is not null && (value.Command is null || value.Receipt is null
            || value.Command.CommandId != id || value.Command.JobId == Guid.Empty || value.Command.Revision < 1 || value.Command.ExpiresUtc == default
            || value.Receipt.CommandId != id || !value.Receipt.Valid()))
            throw new InvalidDataException("Повреждён журнал удалённых запусков. Проверьте историю перед повторным запуском.");
        return value;
    }
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();
    private async Task DeliverAsync(RemoteCommandJournal value, CancellationToken token)
    {
        if (value.Delivered) return;
        try { await api.SendCommandReceiptAsync(settings, value.Receipt, token); }
        catch (HttpRequestException e) when (value.Receipt.Terminal && e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.Conflict)
        { /* A final server decision or removed enrollment cannot be overwritten; retain the local tombstone. */ }
        await WriteAsync(value with { Delivered = true }, token);
    }
    public async Task PollOnceAsync(CancellationToken token = default)
    {
        JournalWarning = false;
        var corrupt = new HashSet<Guid>();
        var attempts = 0;
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order().Take(10001))
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id)) continue;
                RemoteCommandJournal? saved;
                try { saved = await ReadAsync(id, token); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
                { corrupt.Add(id); JournalWarning = true; continue; }
                if (saved is null) continue;
                if (saved.Receipt.Status == "Accepted" && saved.Command.ExpiresUtc <= Now)
                {
                    saved = saved with { Receipt = new(id, "Expired", Reason: "DeadlineElapsed"), Delivered = false };
                    await WriteAsync(saved, token);
                }
                if (saved.Receipt.Status == "Running")
                {
                    saved = saved with { Receipt = new(id, "Interrupted", Reason: "AgentRestarted"), Delivered = false };
                    await WriteAsync(saved, token);
                }
                if (saved.Receipt.Terminal && !saved.Delivered && attempts++ < 16)
                {
                    try { await DeliverAsync(saved, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception) { JournalWarning = true; }
                }
            }
        }
        foreach (var command in await api.PollCommandsAsync(settings, token))
        {
            if (corrupt.Contains(command.CommandId)) continue;
            var saved = await ReadAsync(command.CommandId, token);
            if (saved is not null && saved.Command != command) throw new InvalidDataException("Команда изменилась без нового идентификатора.");
            if (saved?.Receipt.Terminal == true) { await DeliverAsync(saved with { Delivered = false }, token); continue; }
            if (saved is null)
            {
                Directory.CreateDirectory(directory);
                if (Directory.EnumerateFiles(directory, "*.json").Take(10000).Count() >= 10000)
                    throw new IOException("Журнал удалённых запусков заполнен. Локальное расписание продолжает работать.");
                saved = new(command, new(command.CommandId, "Accepted"));
                await WriteAsync(saved, token);
            }
            if (command.ExpiresUtc <= Now)
            {
                saved = saved with { Receipt = new(command.CommandId, "Expired", Reason: "DeadlineElapsed"), Delivered = false };
                await WriteAsync(saved, token); await DeliverAsync(saved, token); continue;
            }
            try { await api.SendCommandReceiptAsync(settings, saved.Receipt, token); }
            catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Gone or HttpStatusCode.NotFound)
            {
                saved = saved with { Receipt = new(command.CommandId, "Rejected", Reason: "RevisionChanged"), Delivered = false };
                await WriteAsync(saved, token); await DeliverAsync(saved, token); continue;
            }
            var started = false;
            try
            {
                var run = await dispatcher.ExecuteCommandAsync(async cancellation =>
                {
                    var snapshot = await source.LoadSnapshotAsync(cancellation);
                    var job = snapshot.Jobs.SingleOrDefault(j => j.Id == command.JobId);
                    var reason = snapshot.Revision != command.Revision ? "RevisionChanged" : job is null ? "JobUnavailable" : null;
                    if (command.ExpiresUtc <= Now || reason is not null)
                    {
                        saved = saved with { Receipt = reason is null ? new(command.CommandId, "Expired", Reason: "DeadlineElapsed")
                            : new(command.CommandId, "Rejected", Reason: reason), Delivered = false };
                        await WriteAsync(saved, cancellation);
                        return null;
                    }
                    // Commit before asking permission to start. A lost reply is uncertain, never permission to replay.
                    saved = saved with { Receipt = new(command.CommandId, "Running"), Delivered = false };
                    await WriteAsync(saved, cancellation);
                    started = true;
                    await api.SendCommandReceiptAsync(settings, saved.Receipt, cancellation);
                    if (command.ExpiresUtc <= Now) throw new TimeoutException();
                    return job;
                }, readRun, token);
                if (run is not null)
                    saved = saved with { Receipt = new(command.CommandId, run.Status == BackupRunStatus.Succeeded ? "Succeeded" : "Failed", run.RunId), Delivered = false };
            }
            catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
            {
                saved = saved with { Receipt = new(command.CommandId, started ? "Interrupted" : "Rejected", Reason: "ExecutionFailed"), Delivered = false };
            }
            catch (OperationCanceledException)
            {
                await WriteAsync(saved with { Receipt = new(command.CommandId, "Interrupted", Reason: "AgentRestarted"), Delivered = false }, CancellationToken.None);
                throw;
            }
            await WriteAsync(saved, CancellationToken.None);
            await DeliverAsync(saved, token);
        }
    }
    public async Task RunAsync(Action<Exception?> warning, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            try { await PollOnceAsync(token); warning(JournalWarning ? new IOException("Часть результатов команд ожидает передачи или проверки локального журнала.") : null); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { warning(new IOException("Удалённые команды ожидают связи или проверки журнала. Локальное расписание продолжает работать.")); }
        } while (await timer.WaitForNextTickAsync(token));
    }
}
