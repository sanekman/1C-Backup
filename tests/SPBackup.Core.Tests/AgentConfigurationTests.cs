using System.Text.Json;
using System.Text.Json.Nodes;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class AgentConfigurationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OneCBackup-agent-" + Guid.NewGuid());
    private readonly AgentSettings managed = new() { Mode = AgentMode.Managed, ServerAddress = "https://backup.example.com/", CredentialKey = "fixture" };
    private JobStore Local => new(Path.Combine(directory, "jobs.json"));
    private AgentConfigurationCache Cache => new(Path.Combine(directory, "server-jobs.json"));
    private BackupJob Job(string name) => new() { Name = name, Source = "database", DestinationDirectory = directory };

    public AgentConfigurationTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task StandaloneUsesLocalJobsAndNeverContactsApi()
    {
        var job = Job("local");
        await Local.SaveAsync([job]);
        var api = new FakeAgentApi();
        var configuration = new AgentConfiguration(Local, Cache, api);
        var settings = managed with { Mode = AgentMode.Standalone, ServerAddress = "" };
        Assert.Equal(job, Assert.Single(await configuration.GetEffectiveJobsAsync(settings)));
        Assert.False((await configuration.SynchronizeAsync(settings, DateTimeOffset.UtcNow)).Enabled);
        await configuration.SendHeartbeatAsync(settings, new(settings.AgentId, "test", false, null, DateTimeOffset.UtcNow));
        Assert.Equal(0, api.Requests);
        Assert.Equal(0, api.Heartbeats);
    }

    [Fact]
    public async Task CachedServerJobsSurviveNetworkFailureAndProcessRestart()
    {
        var local = Job("local");
        var remote = Job("remote");
        await Local.SaveAsync([local]);
        var api = new FakeAgentApi { Configuration = new(managed.AgentId, 1, [remote]) };
        var configuration = new AgentConfiguration(Local, Cache, api);
        Assert.True((await configuration.SynchronizeAsync(managed, DateTimeOffset.UtcNow)).Succeeded);
        api.Error = new HttpRequestException("offline fixture");
        Assert.False((await configuration.SynchronizeAsync(managed, DateTimeOffset.UtcNow)).Succeeded);
        Assert.Equal(remote, Assert.Single(await new AgentConfiguration(Local, Cache).GetEffectiveJobsAsync(managed)));
        Assert.Equal(local, Assert.Single(await Local.LoadAsync()));
        Assert.Equal(local, Assert.Single(await configuration.GetEffectiveJobsAsync(managed with { Mode = AgentMode.Standalone })));
    }

    [Fact]
    public async Task ManagedWithoutCacheDoesNotFallBackToUnrelatedLocalJobs()
    {
        await Local.SaveAsync([Job("local")]);
        Assert.Empty(await new AgentConfiguration(Local, Cache).GetEffectiveJobsAsync(managed));
    }

    [Fact]
    public async Task ChangingServerOrAgentDoesNotReusePreviousCache()
    {
        var api = new FakeAgentApi { Configuration = new(managed.AgentId, 1, [Job("remote")]) };
        var configuration = new AgentConfiguration(Local, Cache, api);
        await configuration.SynchronizeAsync(managed, DateTimeOffset.UtcNow);
        Assert.Empty(await configuration.GetEffectiveJobsAsync(managed with { ServerAddress = "https://another.example.com/" }));
        Assert.Empty(await configuration.GetEffectiveJobsAsync(managed with { AgentId = Guid.NewGuid() }));
    }

    [Theory]
    [InlineData("older")]
    [InlineData("same-revision-changed")]
    [InlineData("other-agent")]
    [InlineData("invalid-job")]
    [InlineData("unsupported-protocol")]
    [InlineData("duplicate-job")]
    public async Task InvalidServerConfigurationDoesNotReplaceCache(string fault)
    {
        var remote = Job("remote");
        var api = new FakeAgentApi { Configuration = new(managed.AgentId, 2, [remote]) };
        var configuration = new AgentConfiguration(Local, Cache, api);
        await configuration.SynchronizeAsync(managed, DateTimeOffset.UtcNow);
        api.Configuration = fault switch
        {
            "older" => new(managed.AgentId, 1, [remote]),
            "same-revision-changed" => new(managed.AgentId, 2, [remote with { Name = "changed" }]),
            "other-agent" => new(Guid.NewGuid(), 3, [remote]),
            "invalid-job" => new(managed.AgentId, 3, [remote with { KeepLast = 0 }]),
            "unsupported-protocol" => new(managed.AgentId, 3, [remote], 2),
            _ => new(managed.AgentId, 3, [remote, remote])
        };
        var result = await configuration.SynchronizeAsync(managed, DateTimeOffset.UtcNow);
        Assert.False(result.Succeeded);
        Assert.IsType<InvalidDataException>(result.Error);
        Assert.Equal(remote, Assert.Single(await configuration.GetEffectiveJobsAsync(managed)));
        Assert.Equal(2, await configuration.GetActiveRevisionAsync(managed));
    }

    [Fact]
    public async Task EmptyNewRevisionExplicitlyRemovesServerJobs()
    {
        var api = new FakeAgentApi { Configuration = new(managed.AgentId, 1, [Job("remote")]) };
        var configuration = new AgentConfiguration(Local, Cache, api);
        await configuration.SynchronizeAsync(managed, DateTimeOffset.UtcNow);
        api.Configuration = new(managed.AgentId, 2, []);
        Assert.True((await configuration.SynchronizeAsync(managed, DateTimeOffset.UtcNow)).Succeeded);
        Assert.Empty(await configuration.GetEffectiveJobsAsync(managed));
    }

    [Fact]
    public async Task NotModifiedRequiresMatchingCachedRevision()
    {
        var api = new FakeAgentApi { Configuration = null };
        var configuration = new AgentConfiguration(Local, Cache, api);
        Assert.False((await configuration.SynchronizeAsync(managed, DateTimeOffset.UtcNow)).Succeeded);
        api.Configuration = new(managed.AgentId, 1, [Job("remote")]);
        await configuration.SynchronizeAsync(managed, DateTimeOffset.UtcNow);
        api.Configuration = null;
        Assert.True((await configuration.SynchronizeAsync(managed, DateTimeOffset.UtcNow)).Succeeded);
        Assert.Equal(1, api.LastRevision);
    }

    [Fact]
    public async Task CancellationPreservesCache()
    {
        var remote = Job("remote");
        var api = new FakeAgentApi { Configuration = new(managed.AgentId, 1, [remote]) };
        var configuration = new AgentConfiguration(Local, Cache, api);
        await configuration.SynchronizeAsync(managed, DateTimeOffset.UtcNow);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => configuration.SynchronizeAsync(managed, DateTimeOffset.UtcNow, cancellation.Token));
        Assert.Equal(remote, Assert.Single(await configuration.GetEffectiveJobsAsync(managed)));
    }

    [Fact]
    public async Task SettingsPersistIdentityAndStoreOnlyCredentialReference()
    {
        var path = Path.Combine(directory, "agent.json");
        var first = await new AgentSettingsStore(path).LoadAsync();
        Assert.Equal(AgentMode.Standalone, first.Mode);
        Assert.Equal(first.AgentId, (await new AgentSettingsStore(path).LoadAsync()).AgentId);
        await new AgentSettingsStore(path).SaveAsync(managed);
        Assert.Equal(managed, await new AgentSettingsStore(path).LoadAsync());
        Assert.DoesNotContain("Password", await File.ReadAllTextAsync(path));
        Assert.DoesNotContain("Bearer", await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("http://backup.example.com")]
    [InlineData("https://user:password@backup.example.com")]
    [InlineData("https://backup.example.com/?token=value")]
    [InlineData("https://backup.example.com/#fragment")]
    public void ManagedRequiresCleanHttpsAddress(string address) =>
        Assert.NotEmpty((managed with { ServerAddress = address }).Validate());

    public void Dispose() => Directory.Delete(directory, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownCachedPolicyCannotBeSilentlyDroppedOnRestart(bool nestedJob)
    {
        var local = Job("local"); await Local.SaveAsync([local]);
        await Cache.SaveAsync(new(managed.NormalizedServerAddress, new(managed.AgentId, 1, [Job("remote")]), DateTimeOffset.UtcNow));
        var path = Path.Combine(directory, "server-jobs.json");
        var snapshot = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var container = nestedJob ? snapshot["Configuration"]!["Jobs"]![0]! : snapshot;
        container["FutureRequiredSecurityPolicy"] = true;
        await File.WriteAllTextAsync(path, snapshot.ToJsonString());
        var original = await File.ReadAllBytesAsync(path);
        var configuration = new AgentConfiguration(Local, Cache);
        await Assert.ThrowsAsync<JsonException>(() => configuration.GetEffectiveJobsAsync(managed));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Equal(local, Assert.Single(await configuration.GetEffectiveJobsAsync(managed with { Mode = AgentMode.Standalone })));
    }
}

internal sealed class FakeAgentApi : IAgentApi
{
    public ServerConfiguration? Configuration { get; set; }
    public Exception? Error { get; set; }
    public Func<CancellationToken, Task<ServerConfiguration?>>? GetOverride { get; set; }
    public int Requests { get; private set; }
    public int Heartbeats { get; private set; }
    public long? LastRevision { get; private set; }

    public Task<ServerConfiguration?> GetConfigurationAsync(AgentSettings settings, long? currentRevision,
        CancellationToken cancellationToken = default)
    {
        Requests++; LastRevision = currentRevision;
        if (GetOverride is not null) return GetOverride(cancellationToken);
        return Error is null ? Task.FromResult(Configuration) : Task.FromException<ServerConfiguration?>(Error);
    }
    public Task SendHeartbeatAsync(AgentSettings settings, AgentHeartbeat heartbeat, CancellationToken cancellationToken = default)
    { Heartbeats++; return Error is null ? Task.CompletedTask : Task.FromException(Error); }
}
