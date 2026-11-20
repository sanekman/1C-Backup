using System.Security.Cryptography;
using System.Text.Json;

namespace SPBackup.Core;

public sealed record LocalJobUpdate(Guid Id, Guid SessionId, string ExpectedHash, string TargetHash, IReadOnlyList<BackupJob> Jobs);
public sealed record LocalJobUpdateReceipt(Guid Id, Guid SessionId, string TargetHash, bool Applied, string? Error);

/// <summary>Session-bound profile publication by the executor that owns the engine lease.</summary>
public sealed class LocalJobUpdates(string directory)
{
    private string PathFor(string kind, Guid id) => Path.Combine(directory, "job-updates", kind, id.ToString("N") + ".json");
    public static string Hash(IReadOnlyList<BackupJob> jobs) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(jobs.OrderBy(job => job.Id).Select(job => job with { TimeZoneId = BackupSchedule.LocalTimeZone }).ToArray())));
    public async Task ApplyAsync(LocalAgentState state, IReadOnlyList<BackupJob> before, IReadOnlyList<BackupJob> after,
        CancellationToken token = default)
    {
        if (!state.CanUpdateLocalJobs || state.Phase != LocalAgentPhase.Running || state.ExecutionMode != AgentMode.Standalone)
            throw new InvalidOperationException("Обновите службу вместе с программой: этот исполнитель не поддерживает применение настроек без остановки.");
        JobStore.ValidateJobs(after);
        var request = new LocalJobUpdate(Guid.NewGuid(), state.SessionId, Hash(before), Hash(after), after.ToArray());
        await new AgentJsonFile<LocalJobUpdate>(PathFor("pending", request.Id)).WriteAsync(request, token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            while (true)
            {
                var result = await new AgentJsonFile<LocalJobUpdateReceipt>(PathFor("receipts", request.Id)).ReadBoundedAsync(8192, timeout.Token);
                if (result is not null)
                {
                    if (result.Id != request.Id || result.SessionId != request.SessionId || result.TargetHash != request.TargetHash)
                        throw new InvalidDataException("Подтверждение относится к другому изменению настроек.");
                    if (!result.Applied) throw new IOException(result.Error == "profile_changed"
                        ? "Задания изменились в другом окне. Перечитайте настройки и повторите изменение."
                        : "Служба не подтвердила применение настроек. Перечитайте задания и проверьте состояние исполнителя.");
                    return;
                }
                var live = await new LocalAgentControlStore(directory).ReadAsync(timeout.Token);
                if (live?.SessionId != request.SessionId || live.Phase != LocalAgentPhase.Running)
                    throw new IOException("Сеанс службы изменился. Перечитайте задания, чтобы проверить результат сохранения.");
                await Task.Delay(100, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException("Служба не подтвердила сохранение вовремя. Перечитайте задания перед повтором; текущая копия не прерывалась."); }
    }
    internal async Task ProcessAsync(Guid session, JobStore store, bool allowed, CancellationToken token)
    {
        var pending = Path.Combine(directory, "job-updates", "pending");
        if (!Directory.Exists(pending)) return;
        foreach (var file in Directory.EnumerateFiles(pending, "*.json").Take(32))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var id)) continue;
            LocalJobUpdate? request;
            try { request = await new AgentJsonFile<LocalJobUpdate>(file).ReadBoundedAsync(16 * 1024 * 1024, token); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { File.Delete(file); continue; }
            if (request is null || request.Id != id || request.SessionId == Guid.Empty || request.Jobs is null
                || request.ExpectedHash is not { Length: 64 } || request.TargetHash is not { Length: 64 }
                || !request.ExpectedHash.All(Uri.IsHexDigit) || !request.TargetHash.All(Uri.IsHexDigit)) { File.Delete(file); continue; }
            var applied = false; string? error = null;
            try
            {
                if (!allowed || request.SessionId != session) error = "session_changed";
                else
                {
                    JobStore.ValidateJobs(request.Jobs);
                    if (Hash(request.Jobs) != request.TargetHash) throw new InvalidDataException("Invalid profile fingerprint.");
                    var actual = Hash(await store.LoadAsync(token));
                    if (actual == request.TargetHash) applied = true; // Crash after publication but before acknowledgment.
                    else if (actual != request.ExpectedHash) error = "profile_changed";
                    else { await store.SaveAsync(request.Jobs, token); applied = true; }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
            {
                try { applied = Hash(await store.LoadAsync(token)) == request.TargetHash; }
                catch (Exception read) when (read is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or JsonException) { }
                if (!applied) error = "write_failed";
            }
            // Publish acknowledgment durably before consuming; replay never repeats a backup.
            await new AgentJsonFile<LocalJobUpdateReceipt>(PathFor("receipts", id)).WriteAsync(
                new(id, request.SessionId, request.TargetHash, applied, error), token);
            File.Delete(file);
        }
    }
}
