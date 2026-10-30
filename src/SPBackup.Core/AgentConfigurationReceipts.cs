using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SPBackup.Core;

public sealed record AgentConfigurationOffer(long? Revision, Guid? OfferId, bool NotModified, ServerConfiguration? Configuration, Exception? Error);
public interface IAgentConfigurationReceiptApi
{
    Task<AgentConfigurationOffer> GetConfigurationOfferAsync(AgentSettings settings, long? revision, CancellationToken token = default);
    Task SendConfigurationReceiptAsync(AgentSettings settings, ConfigurationReceipt receipt, CancellationToken token = default);
}
internal sealed record PendingConfigurationReceipt(string ServerAddress, ConfigurationReceipt Receipt);

internal sealed class ConfigurationReceiptOutbox(string directory)
{
    private static readonly JsonSerializerOptions Json = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true, AllowDuplicateProperties = false };
    private string PathFor(Guid offer) => Path.Combine(directory, offer.ToString("D") + ".json");
    private string[] Files()
    {
        if (!Directory.Exists(directory)) return [];
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Недопустимый каталог подтверждений.");
        var files = Directory.EnumerateFiles(directory, "*.json").Where(p => Guid.TryParseExact(Path.GetFileNameWithoutExtension(p), "D", out _)).Take(65).ToArray();
        if (files.Length > 64) throw new IOException("Очередь подтверждений переполнена.");
        return files;
    }
    internal async Task SaveAsync(AgentSettings settings, ConfigurationReceipt receipt, CancellationToken token)
    {
        if (!receipt.Valid() || receipt.AgentId != settings.AgentId) throw new InvalidDataException("Некорректное подтверждение.");
        var path = PathFor(receipt.OfferId);
        if (Files().Length >= 64 && !File.Exists(path)) throw new IOException("Очередь подтверждений переполнена.");
        await new AgentJsonFile<PendingConfigurationReceipt>(path).WriteAsync(new(settings.NormalizedServerAddress, receipt), token);
    }
    internal async Task<bool> FlushAsync(AgentSettings settings, IAgentConfigurationReceiptApi api, CancellationToken token)
    {
        var pending = false;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var candidates = new List<(string Path, PendingConfigurationReceipt Value)>();
            foreach (var path in Files())
            {
                try
                {
                    await using var input = await AtomicProfileFile.OpenReadAsync(path, budget.Token);
                    if (input is null) continue;
                    if (input.Length > 8192) throw new InvalidDataException();
                    var value = await JsonSerializer.DeserializeAsync<PendingConfigurationReceipt>(input, Json, budget.Token);
                    if (value is null || value.Receipt is null || !value.Receipt.Valid()
                        || Path.GetFileNameWithoutExtension(path) != value.Receipt.OfferId.ToString("D")) throw new InvalidDataException();
                    if (value.ServerAddress == settings.NormalizedServerAddress && value.Receipt.AgentId == settings.AgentId) candidates.Add((path, value));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { pending = true; }
            }
            var ordered = candidates.OrderByDescending(c => c.Value.Receipt.Revision).ThenByDescending(c => c.Value.Receipt.ObservedUtc).ToArray();
            if (ordered.Length > 4) pending = true;
            foreach (var item in ordered.Take(4))
            {
                try { await api.SendConfigurationReceiptAsync(settings, item.Value.Receipt, budget.Token); }
                catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) { /* Superseded enrollment/offer. A new offer can reconstruct the receipt. */ }
                File.Delete(item.Path); // A crash before this point replays the same idempotent decision.
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { pending = true; } // An unavailable receipt channel must not discard valid offline jobs.
        return pending;
    }
}
