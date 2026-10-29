using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class AgentExecutionJobSourceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sp-effective-jobs-" + Guid.NewGuid().ToString("N"));
    private readonly AgentSettings settings = new() { Mode = AgentMode.Managed, ServerAddress = "https://backup.example.test/", CredentialKey = "test" };
    private JobStore Local => new(Path.Combine(root, "jobs.json"));
    private AgentConfigurationCache Cache => new(Path.Combine(root, "server-jobs.json"));
    private BackupJob Job => new() { Name = "Server", Kind = DatabaseKind.OneCFile,
        OneCMethod = OneCBackupMethod.FileArchive, Source = Path.Combine(root, "source"),
        DestinationDirectory = Path.Combine(root, "remote-copies"), DailyAt = new(12, 0) };
    private AgentConfiguration Configuration(Api? api = null) => new(Local, Cache, api);
    private AgentExecutionJobSource Source(AgentConfiguration configuration) => new(configuration, settings);
    public AgentExecutionJobSourceTests() => Directory.CreateDirectory(root);

    private sealed class Api : IAgentApi
    {
        public ServerConfiguration Document = null!;
        public Task<ServerConfiguration?> GetConfigurationAsync(AgentSettings settings, long? revision, CancellationToken token)
            => Task.FromResult<ServerConfiguration?>(Document);
        public Task SendHeartbeatAsync(AgentSettings settings, AgentHeartbeat heartbeat, CancellationToken token) => Task.CompletedTask;
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("agent")]
    [InlineData("server")]
    public async Task MissingOrForeignCacheNeverRunsLocalJobWithTheSameId(string fault)
    {
        var job = Job;
        await Local.SaveAsync([job with { Name = "Local" }]);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(root, "jobs.json"));
        if (fault != "missing") await Cache.SaveAsync(new(fault == "server" ? "https://other.example.test/" : settings.NormalizedServerAddress,
            new(fault == "agent" ? Guid.NewGuid() : settings.AgentId, 1, [job]), DateTimeOffset.UtcNow));
        var snapshot = await Configuration().GetSnapshotAsync(settings);
        Assert.Null(snapshot.Revision);
        Assert.Empty(snapshot.Jobs);
        var dispatcher = new LocalBackupDispatcher(Source(Configuration()), Path.Combine(root, "managed-startup.json"),
            (_, _) => throw new InvalidOperationException("No backup may start"));
        Assert.Empty(await dispatcher.LoadScheduledJobsAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => dispatcher.ExecuteByIdAsync(job.Id));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(root, "jobs.json")));
    }

    [Theory]
    [InlineData(AgentMode.Standalone)]
    [InlineData(AgentMode.Managed)]
    public async Task SnapshotContainsOneRevisionAndReadOnlyNestedCollections(AgentMode mode)
    {
        var job = Job with { AdditionalDailyTimes = new List<TimeOnly> { new(13, 0) },
            AdditionalDestinations = new List<BackupDestination> { new() { DestinationDirectory = Path.Combine(root, "second") } } };
        await Local.SaveAsync([job]);
        await Cache.SaveAsync(new(settings.NormalizedServerAddress, new(settings.AgentId, 7, [job]), DateTimeOffset.UtcNow));
        var snapshot = await Configuration().GetSnapshotAsync(settings with { Mode = mode });
        Assert.Equal(mode == AgentMode.Managed ? 7L : (long?)null, snapshot.Revision);
        Assert.Equal(settings.AgentId, snapshot.AgentId);
        Assert.Equal(mode == AgentMode.Managed ? settings.NormalizedServerAddress : null, snapshot.ServerAddress);
        var frozen = Assert.Single(snapshot.Jobs);
        Assert.Throws<NotSupportedException>(() => ((IList<BackupJob>)snapshot.Jobs)[0] = job with { Name = "Changed" });
        Assert.Throws<NotSupportedException>(() => ((IList<TimeOnly>)frozen.AdditionalDailyTimes!)[0] = new(14, 0));
        Assert.Throws<NotSupportedException>(() => ((IList<BackupDestination>)frozen.AdditionalDestinations!).Clear());
        await Cache.SaveAsync(new(settings.NormalizedServerAddress, new(settings.AgentId, 8, []), DateTimeOffset.UtcNow));
        Assert.Single(snapshot.Jobs);
        Assert.Equal("Server", frozen.Name);
    }

    [Fact]
    public async Task RevisionChangeDoesNotAlterActiveCopyAndManualRunUsesNewSnapshot()
    {
        var old = Job;
        var api = new Api { Document = new(settings.AgentId, 1, [old]) };
        var configuration = Configuration(api);
        Assert.True((await configuration.SynchronizeAsync(settings, DateTimeOffset.UtcNow)).Succeeded);
        var source = Source(configuration);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = new List<string>();
        var dispatcher = new LocalBackupDispatcher(source, Path.Combine(root, "managed-startup.json"), async (job, token) =>
        {
            executed.Add(job.Name);
            if (job.Name == old.Name) { entered.SetResult(); await release.Task.WaitAsync(token); Assert.Equal(old.Name, job.Name); }
        });
        var first = dispatcher.ExecuteByIdAsync(old.Id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            api.Document = new(settings.AgentId, 2, [old with { Name = "New", ScheduleKind = ScheduleKind.Manual }]);
            Assert.True((await configuration.SynchronizeAsync(settings, DateTimeOffset.UtcNow)).Succeeded);
            var second = dispatcher.ExecuteByIdAsync(old.Id);
            Assert.False(second.IsCompleted);
            release.SetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(new[] { "Server", "New" }, executed);
            Assert.Equal(2, (await configuration.GetSnapshotAsync(settings)).Revision);
            api.Document = new(settings.AgentId, 3, []);
            Assert.True((await configuration.SynchronizeAsync(settings, DateTimeOffset.UtcNow)).Succeeded);
            Assert.Empty(await dispatcher.LoadScheduledJobsAsync());
            await Assert.ThrowsAsync<InvalidDataException>(() => dispatcher.ExecuteByIdAsync(old.Id));
        }
        finally { release.TrySetResult(); await first; }
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    }

    [Fact]
    public async Task ConcurrentPublicationKeepsRevisionAndJobsInTheSameSnapshot()
    {
        await Cache.SaveAsync(new(settings.NormalizedServerAddress, new(settings.AgentId, 1, [Job with { Name = "1" }]), DateTimeOffset.UtcNow));
        var configuration = Configuration();
        async Task Publish()
        {
            for (var revision = 2; revision <= 20; revision++)
                await Cache.SaveAsync(new(settings.NormalizedServerAddress,
                    new(settings.AgentId, revision, [Job with { Name = revision.ToString() }]), DateTimeOffset.UtcNow));
        }
        async Task Observe()
        {
            for (var index = 0; index < 40; index++)
            {
                var snapshot = await configuration.GetSnapshotAsync(settings);
                Assert.Equal(snapshot.Revision.ToString(), Assert.Single(snapshot.Jobs).Name);
            }
        }
        await Task.WhenAll(Publish(), Observe());
        Assert.Equal(20, (await configuration.GetSnapshotAsync(settings)).Revision);
    }

    [Fact]
    public async Task CorruptServerCacheStopsReadingInsteadOfUsingLocalJobs()
    {
        await Local.SaveAsync([Job with { Name = "Local" }]);
        await File.WriteAllTextAsync(Path.Combine(root, "server-jobs.json"), "{invalid");
        var dispatcher = new LocalBackupDispatcher(Source(Configuration()), Path.Combine(root, "managed-startup.json"),
            (_, _) => throw new InvalidOperationException("No backup may start"));
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => dispatcher.LoadScheduledJobsAsync());
        Assert.Equal("Local", Assert.Single(await Local.LoadAsync()).Name);
    }

    [Fact]
    public async Task RestartedOfflineSourceReachesRealArchiveRunnerWithoutTouchingLocalJobs()
    {
        var job = Job;
        Directory.CreateDirectory(job.Source);
        await File.WriteAllTextAsync(Path.Combine(job.Source, "1Cv8.1CD"), "closed database fixture");
        await Local.SaveAsync([job with { Name = "Local", DestinationDirectory = Path.Combine(root, "local-copies") }]);
        var original = await File.ReadAllBytesAsync(Path.Combine(root, "jobs.json"));
        var api = new Api { Document = new(settings.AgentId, 5, [job]) };
        Assert.True((await Configuration(api).SynchronizeAsync(settings, DateTimeOffset.UtcNow)).Succeeded);
        var restarted = Configuration(); // No network adapter: scheduling must use the durable cache.
        var history = new BackupRunHistory(Path.Combine(root, "runs.json"));
        var runner = new LocalBackupRunner(new OneCFileArchiveExecutor(),
            new BackupCompletion(new FileBackupStorage(), new BackupCatalog(Path.Combine(root, "copies.json"))), history, Path.Combine(root, "work"));
        var dispatcher = new LocalBackupDispatcher(Source(restarted), Path.Combine(root, "managed-startup.json"),
            async (snapshot, token) => { await runner.RunAsync(snapshot, token); });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.RunScheduleAsync(
            _ => { stop.Cancel(); return Task.CompletedTask; }, stop.Token, timeProvider: new Clock()));
        var run = Assert.Single(await history.ReadAsync());
        Assert.Equal(BackupRunStatus.Succeeded, run.Status);
        Assert.True(File.Exists(run.Backup!.Location));
        Assert.False(Directory.Exists(Path.Combine(root, "local-copies")));
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(root, "jobs.json")));
        Assert.Equal(5, (await restarted.GetSnapshotAsync(settings)).Revision);
    }

    [Fact]
    public async Task StorageProbeUsesServerSnapshotInsteadOfLocalJobWithSameId()
    {
        var job = Job;
        await Local.SaveAsync([job with { DestinationDirectory = Path.Combine(root, "local-copies") }]);
        await Cache.SaveAsync(new(settings.NormalizedServerAddress, new(settings.AgentId, 1, [job]), DateTimeOffset.UtcNow));
        var session = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await new LocalAgentControlStore(root).WriteAsync(new(session, Environment.ProcessId, now, now, LocalAgentPhase.Running) { CanCheckStorages = true });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var loop = new LocalStorageCheckProcessor(root, Source(Configuration()), new(new FileBackupStorage(), Path.Combine(root, "work")))
            .RunAsync(session, _ => { }, stop.Token);
        try
        {
            var queue = new LocalStorageCheckQueue(root);
            var report = await queue.WaitAsync(await queue.EnqueueAsync(session, job), stop.Token);
            Assert.True(Assert.Single(report.Destinations).Succeeded);
            Assert.True(Directory.Exists(job.DestinationDirectory));
            Assert.False(Directory.Exists(Path.Combine(root, "local-copies")));
        }
        finally { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop); }
    }

    public void Dispose() => Directory.Delete(root, true);
}
