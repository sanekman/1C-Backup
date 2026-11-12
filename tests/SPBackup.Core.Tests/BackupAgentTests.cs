using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class BackupAgentTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OneCBackup-runtime-" + Guid.NewGuid());
    private readonly AgentSettings settings = new() { Mode = AgentMode.Managed, ServerAddress = "https://backup.example.com/", CredentialKey = "fixture" };
    private readonly DateTimeOffset now = new(2026, 10, 10, 23, 0, 0, TimeSpan.Zero);
    private BackupJob Job => new() { Name = "test", Source = "database", DestinationDirectory = directory, DailyAt = new TimeOnly(23, 0) };
    private AgentConfiguration Configuration(FakeAgentApi api) => new(new JobStore(Path.Combine(directory, "local.json")),
        new AgentConfigurationCache(Path.Combine(directory, "cache.json")), api);

    public BackupAgentTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task OfflineServerDoesNotStopCachedScheduledJob()
    {
        var job = Job;
        var api = new FakeAgentApi { Configuration = new(settings.AgentId, 1, [job]) };
        var agent = new BackupAgent(Configuration(api), "test");
        Assert.True((await agent.ConnectOnceAsync(settings, now.AddMinutes(-1)))!.Synchronization.Succeeded);
        api.Error = new HttpRequestException("offline fixture");
        var connection = await agent.ConnectOnceAsync(settings, now);
        Assert.False(connection!.Synchronization.Succeeded);
        Assert.NotNull(connection.HeartbeatError);
        var executed = 0;
        Assert.Single(await agent.RunScheduledOnceAsync(settings, now, (_, _) => { executed++; return Task.CompletedTask; }));
        Assert.Equal(1, executed);
    }

    [Fact]
    public async Task HangingApiDoesNotBlockIndependentScheduler()
    {
        var api = new FakeAgentApi { Configuration = new(settings.AgentId, 1, [Job]) };
        var agent = new BackupAgent(Configuration(api), "test");
        await agent.ConnectOnceAsync(settings, now.AddMinutes(-1));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<ServerConfiguration?>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.GetOverride = token => { started.SetResult(); return never.Task.WaitAsync(token); };
        using var stop = new CancellationTokenSource();
        var connection = agent.ConnectOnceAsync(settings, now, cancellationToken: stop.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var execution = await agent.RunScheduledOnceAsync(settings, now, (_, _) => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(execution);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection);
    }

    [Fact]
    public async Task PollingRespectsIntervalAndStandaloneDoesNotConnect()
    {
        var api = new FakeAgentApi { Configuration = new(settings.AgentId, 1, []) };
        var agent = new BackupAgent(Configuration(api), "test");
        await agent.ConnectOnceAsync(settings, now);
        Assert.Null(await agent.ConnectOnceAsync(settings, now.AddSeconds(30)));
        Assert.Equal(1, api.Requests);
        await agent.ConnectOnceAsync(settings, now.AddSeconds(60));
        Assert.Equal(2, api.Requests);
        Assert.Equal(2, api.Heartbeats);
        Assert.Null(await agent.ConnectOnceAsync(settings with { Mode = AgentMode.Standalone }, now.AddSeconds(61)));
        Assert.Equal(2, api.Requests);
    }

    [Fact]
    public async Task AutonomousAgentExecutesLocalScheduleWithoutApi()
    {
        var store = new JobStore(Path.Combine(directory, "local.json"));
        await store.SaveAsync([Job]);
        var agent = new BackupAgent(new AgentConfiguration(store, new AgentConfigurationCache(Path.Combine(directory, "cache.json"))), "test");
        Assert.Single(await agent.RunScheduledOnceAsync(new AgentSettings(), now, (_, _) => Task.CompletedTask));
    }

    public void Dispose() => Directory.Delete(directory, true);
}
