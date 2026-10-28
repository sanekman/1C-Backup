using System.Text.Json;
using System.Text.Json.Serialization;

namespace SPBackup.Core;

public sealed record ServerConfiguration(Guid AgentId, long Revision, IReadOnlyList<BackupJob> Jobs, int ProtocolVersion = 1);
public sealed record CachedAgentConfiguration(string ServerAddress, ServerConfiguration Configuration, DateTimeOffset ReceivedUtc);
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
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public async Task<CachedAgentConfiguration?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var stream = await AtomicProfileFile.OpenReadAsync(path, cancellationToken);
        if (stream is null) return null;
        var cached = await JsonSerializer.DeserializeAsync<CachedAgentConfiguration>(stream, ReadOptions, cancellationToken)
            ?? throw new InvalidDataException("Кэш конфигурации сервера содержит null.");
        Validate(cached.Configuration);
        return cached;
    }

    public Task SaveAsync(CachedAgentConfiguration cached, CancellationToken cancellationToken = default)
    {
        Validate(cached.Configuration);
        return file.WriteAsync(cached, cancellationToken);
    }

    internal static void Validate(ServerConfiguration configuration)
    {
        if (configuration is null || configuration.ProtocolVersion != 1 || configuration.AgentId == Guid.Empty ||
            configuration.Revision < 1 || configuration.Jobs is null || configuration.Jobs.Count > 10000)
            throw new InvalidDataException("Некорректная конфигурация сервера.");
        JobStore.ValidateJobs(configuration.Jobs);
    }
}

public sealed record AgentSyncResult(bool Enabled, bool Succeeded, long? Revision, Exception? Error);

/// <summary>Server configuration and local jobs are separate; offline never means an empty schedule.</summary>
public sealed class AgentConfiguration(JobStore localJobs, AgentConfigurationCache cache, IAgentApi? api = null)
{
    private readonly SemaphoreSlim syncGate = new(1, 1);

    public async Task<IReadOnlyList<BackupJob>> GetEffectiveJobsAsync(AgentSettings settings,
        CancellationToken cancellationToken = default)
    {
        ValidateSettings(settings);
        if (settings.Mode == AgentMode.Standalone) return await localJobs.LoadAsync(cancellationToken);
        var cached = await cache.LoadAsync(cancellationToken);
        return Matches(cached, settings) ? cached!.Configuration.Jobs : [];
    }

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
        try
        {
            var cached = await cache.LoadAsync(cancellationToken);
            if (Matches(cached, settings)) revision = cached!.Configuration.Revision;
            if (api is null) throw new InvalidOperationException("Адаптер API сервера ещё не подключён.");
            var configuration = await api.GetConfigurationAsync(settings, revision, cancellationToken);
            if (configuration is null)
            {
                if (revision is null) throw new InvalidDataException("Сервер не прислал конфигурацию для первого подключения.");
                await cache.SaveAsync(cached! with { ReceivedUtc = now.ToUniversalTime() }, cancellationToken);
                return new(true, true, revision, null);
            }
            AgentConfigurationCache.Validate(configuration);
            if (configuration.AgentId != settings.AgentId)
                throw new InvalidDataException("Сервер вернул настройки другого агента.");
            if (revision is not null && configuration.Revision < revision)
                throw new InvalidDataException("Сервер вернул устаревшую конфигурацию.");
            if (revision == configuration.Revision && !cached!.Configuration.Jobs.OrderBy(j => j.Id)
                .SequenceEqual(configuration.Jobs.OrderBy(j => j.Id)))
                throw new InvalidDataException("Содержимое конфигурации изменилось без новой ревизии.");
            await cache.SaveAsync(new(settings.NormalizedServerAddress, configuration, now.ToUniversalTime()), cancellationToken);
            return new(true, true, configuration.Revision, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new(true, false, revision, ex); }
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
