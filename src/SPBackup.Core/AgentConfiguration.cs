using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

namespace SPBackup.Core;

public sealed record ServerConfiguration(Guid AgentId, long Revision, IReadOnlyList<BackupJob> Jobs, int ProtocolVersion = 1);
public sealed record CachedAgentConfiguration(string ServerAddress, ServerConfiguration Configuration, DateTimeOffset ReceivedUtc)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? OfferId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ConfigurationHash { get; init; }
}
public sealed record AgentHeartbeat(Guid AgentId, string AgentVersion, bool BackupExecutionAvailable,
    long? ConfigurationRevision, DateTimeOffset SentUtc);

/// <summary>The server-specific HTTP adapter implements this contract.</summary>
public interface IAgentApi
{
    Task<ServerConfiguration?> GetConfigurationAsync(AgentSettings settings, long? currentRevision,
        CancellationToken cancellationToken = default);
    Task SendHeartbeatAsync(AgentSettings settings, AgentHeartbeat heartbeat, CancellationToken cancellationToken = default);
}

public sealed class AgentConfigurationCache(string path)
{
    private readonly AgentJsonFile<CachedAgentConfiguration> file = new(path);
    internal ConfigurationReceiptOutbox Receipts { get; } = new(path + ".receipts");
    private static readonly JsonSerializerOptions HashJson = new() { Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };
    internal static string Digest(ServerConfiguration configuration) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(configuration with { Jobs = configuration.Jobs.OrderBy(j => j.Id).ToArray() }, HashJson)));
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public async Task<CachedAgentConfiguration?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var stream = await AtomicProfileFile.OpenReadAsync(path, cancellationToken);
        if (stream is null) return null;
        var cached = await JsonSerializer.DeserializeAsync<CachedAgentConfiguration>(stream, ReadOptions, cancellationToken)
            ?? throw new InvalidDataException("Кэш конфигурации сервера содержит null.");
        Validate(cached.Configuration);
        ValidateReceiptBinding(cached);
        return cached;
    }

    public Task SaveAsync(CachedAgentConfiguration cached, CancellationToken cancellationToken = default)
    {
        Validate(cached.Configuration);
        ValidateReceiptBinding(cached);
        return file.WriteAsync(cached, cancellationToken);
    }

    private static void ValidateReceiptBinding(CachedAgentConfiguration cached)
    {
        if (cached.OfferId is null && cached.ConfigurationHash is null) return; // Legacy cache remains readable.
        if (cached.OfferId is null || cached.OfferId == Guid.Empty
            || cached.ConfigurationHash != Digest(cached.Configuration)) throw new InvalidDataException("Повреждена привязка кэша к предложению сервера.");
    }

    internal static void Validate(ServerConfiguration configuration)
    {
        if (configuration is null || configuration.ProtocolVersion != 1 || configuration.AgentId == Guid.Empty ||
            configuration.Revision < 1 || configuration.Jobs is null || configuration.Jobs.Count > 10000)
            throw new InvalidDataException("Некорректная конфигурация сервера.");
        JobStore.ValidateJobs(configuration.Jobs);
    }
}

public sealed record AgentSyncResult(bool Enabled, bool Succeeded, long? Revision, Exception? Error)
{ public bool ReceiptPending { get; init; } }

/// <summary>Server configuration and local jobs are separate; offline never means an empty schedule.</summary>
public sealed class AgentConfiguration(JobStore localJobs, AgentConfigurationCache cache, IAgentApi? api = null)
{
    private readonly SemaphoreSlim syncGate = new(1, 1);

    public async Task<IReadOnlyList<BackupJob>> GetEffectiveJobsAsync(AgentSettings settings,
        CancellationToken cancellationToken = default) => (await GetSnapshotAsync(settings, cancellationToken)).Jobs;

    /// <summary>Reads the revision and its jobs together; callers never combine two cache generations.</summary>
    public async Task<AgentJobSnapshot> GetSnapshotAsync(AgentSettings settings,
        CancellationToken cancellationToken = default)
    {
        ValidateSettings(settings);
        if (settings.Mode == AgentMode.Standalone)
            return new(settings.Mode, settings.AgentId, null, null, Freeze(await localJobs.LoadAsync(cancellationToken)));
        var cached = await cache.LoadAsync(cancellationToken);
        var matches = Matches(cached, settings);
        return new(settings.Mode, settings.AgentId, settings.NormalizedServerAddress,
            matches ? cached!.Configuration.Revision : null,
            Freeze(matches ? cached!.Configuration.Jobs : []));
    }

    private static IReadOnlyList<BackupJob> Freeze(IReadOnlyList<BackupJob> jobs) => Array.AsReadOnly(jobs.Select(job => job with
    {
        AdditionalDailyTimes = job.AdditionalDailyTimes is null ? null : Array.AsReadOnly(job.AdditionalDailyTimes.ToArray()),
        AdditionalDestinations = job.AdditionalDestinations is null ? null : Array.AsReadOnly(job.AdditionalDestinations.ToArray())
    }).ToArray());

    public async Task<long?> GetActiveRevisionAsync(AgentSettings settings, CancellationToken cancellationToken = default)
    {
        ValidateSettings(settings);
        if (settings.Mode == AgentMode.Standalone) return null;
        var cached = await cache.LoadAsync(cancellationToken);
        return Matches(cached, settings) ? cached!.Configuration.Revision : null;
    }

    public Task SendHeartbeatAsync(AgentSettings settings, AgentHeartbeat heartbeat, CancellationToken cancellationToken = default)
    {
        ValidateSettings(settings);
        if (settings.Mode == AgentMode.Standalone) return Task.CompletedTask;
        if (api is null) throw new InvalidOperationException("Адаптер API сервера ещё не подключён.");
        return api.SendHeartbeatAsync(settings, heartbeat, cancellationToken);
    }

    public async Task<AgentSyncResult> SynchronizeAsync(AgentSettings settings, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ValidateSettings(settings);
        if (settings.Mode == AgentMode.Standalone) return new(false, false, null, null);
        await syncGate.WaitAsync(cancellationToken);
        long? revision = null;
        AgentConfigurationOffer? offer = null;
        var receiptApi = api as IAgentConfigurationReceiptApi;
        var reason = "InvalidConfiguration";
        AgentSyncResult result;
        try
        {
            try
            {
                var cached = await cache.LoadAsync(cancellationToken);
                if (Matches(cached, settings)) revision = cached!.Configuration.Revision;
                if (api is null) throw new InvalidOperationException("Адаптер API сервера ещё не подключён.");
                offer = receiptApi is null ? null : await receiptApi.GetConfigurationOfferAsync(settings, revision, cancellationToken);
                if (offer?.NotModified == true && offer.OfferId is not null && (cached?.OfferId != offer.OfferId || offer.Revision != revision))
                {
                    offer = null;
                    offer = await receiptApi!.GetConfigurationOfferAsync(settings, null, cancellationToken); // Upgrade an unbound legacy cache with a full response.
                }
                if (offer?.Error is not null) throw offer.Error;
                if (offer?.OfferId is not null && (offer.OfferId == Guid.Empty || offer.Revision is null or <= 0))
                { offer = null; throw new InvalidDataException("Не удалось привязать предложение конфигурации."); }
                var configuration = receiptApi is null ? await api.GetConfigurationAsync(settings, revision, cancellationToken) : offer!.Configuration;
                if (configuration is null)
                {
                    if (revision is null) throw new InvalidDataException("Сервер не прислал конфигурацию для первого подключения.");
                    if (offer is { NotModified: false }) throw new InvalidDataException("Сервер не прислал конфигурацию.");
                    if (offer?.OfferId is not null && (offer.Revision != revision || offer.OfferId != cached?.OfferId))
                        throw new InvalidDataException("Предложение не относится к сохранённому кэшу.");
                    reason = "CacheWriteFailed";
                    await cache.SaveAsync(cached! with { ReceivedUtc = now.ToUniversalTime() }, cancellationToken);
                    result = new(true, true, revision, null);
                }
                else
                {
                    if (offer?.OfferId is not null && offer.Revision != configuration.Revision)
                        throw new InvalidDataException("Ревизия предложения не совпадает с конфигурацией.");
                    AgentConfigurationCache.Validate(configuration);
                    if (configuration.AgentId != settings.AgentId)
                        throw new InvalidDataException("Сервер вернул настройки другого агента.");
                    if (revision is not null && configuration.Revision < revision)
                        throw new InvalidDataException("Сервер вернул устаревшую конфигурацию.");
                    if (revision == configuration.Revision && AgentConfigurationCache.Digest(cached!.Configuration) != AgentConfigurationCache.Digest(configuration))
                        throw new InvalidDataException("Содержимое конфигурации изменилось без новой ревизии.");
                    reason = "CacheWriteFailed";
                    await cache.SaveAsync(new(settings.NormalizedServerAddress, configuration, now.ToUniversalTime())
                        { OfferId = offer?.OfferId, ConfigurationHash = offer?.OfferId is null ? null : AgentConfigurationCache.Digest(configuration) }, cancellationToken);
                    result = new(true, true, configuration.Revision, null);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { result = new(true, false, revision, ex); }
            var pending = false;
            if (receiptApi is not null)
            {
                if (offer is { OfferId: { } id, Revision: { } offeredRevision })
                {
                    try { await cache.Receipts.SaveAsync(settings, new(settings.AgentId, offeredRevision, id, result.Succeeded ? "Accepted" : "Rejected", result.Succeeded ? null : reason, now.ToUniversalTime()), cancellationToken); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception) { pending = true; }
                }
                pending |= await cache.Receipts.FlushAsync(settings, receiptApi, cancellationToken);
            }
            return result with { ReceiptPending = pending };
        }
        finally { syncGate.Release(); }
    }

    private static bool Matches(CachedAgentConfiguration? cached, AgentSettings settings) =>
        cached is not null && cached.Configuration.AgentId == settings.AgentId &&
        string.Equals(cached.ServerAddress, settings.NormalizedServerAddress, StringComparison.Ordinal);

    private static void ValidateSettings(AgentSettings settings)
    {
        var errors = settings.Validate();
        if (errors.Count != 0) throw new ArgumentException(string.Join(" ", errors), nameof(settings));
    }
}
