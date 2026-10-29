using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Server.Tests;

public sealed class ManagedHostTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private sealed class Store(string token) : IFtpCredentialStore
    { public Task<NetworkCredential> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(new NetworkCredential("agent", token)); }
    private sealed class BlockingArchive : IBackupExecutor
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> CreateAsync(BackupJob job, string work, CancellationToken token = default)
        { Started.TrySetResult(); await Release.Task.WaitAsync(token); return await new OneCFileArchiveExecutor().CreateAsync(job, work, token); }
    }
    private static async Task Wait(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        while (!await condition()) await Task.Delay(100, timeout.Token);
    }
    private Task Publish(ServerConfiguration configuration) => fixture.ExecuteAsync(
        "UPDATE server_configurations SET revision=@revision,document=@document::jsonb WHERE agent_id=@id",
        ("revision", configuration.Revision), ("document", JsonSerializer.Serialize(configuration,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } })), ("id", configuration.AgentId));

    [ServerFact]
    public async Task ActualHostRunsAcceptedSnapshotWhileNewRevisionArrivesAndReportsItsOriginalRevision()
    {
        var agent = await fixture.EnrollAsync();
        var root = Path.Combine(fixture.Root, "managed-agent"); Directory.CreateDirectory(Path.Combine(root, "source"));
        await File.WriteAllTextAsync(Path.Combine(root, "source", "1Cv8.1CD"), "closed database fixture");
        var job = new BackupJob { Name = "First revision", Kind = DatabaseKind.OneCFile, OneCMethod = OneCBackupMethod.FileArchive,
            Source = Path.Combine(root, "source"), DestinationDirectory = Path.Combine(root, "copies"), ScheduleKind = ScheduleKind.SystemStartup };
        var settings = new AgentSettings { AgentId = agent.AgentId, Mode = AgentMode.Managed,
            ServerAddress = fixture.Address.ToString(), CredentialKey = "host-enrollment", SyncIntervalSeconds = 15 };
        using var cert = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(fixture.Root, "certificate.pfx"),
            File.ReadAllText(Path.Combine(fixture.Root, "certificate-password.txt")));
        var connection = new ServerMonitoringSettings(settings.ServerAddress, agent.AgentId, settings.CredentialKey,
            Convert.ToHexString(SHA256.HashData(cert.RawData)));
        await new AgentSettingsStore(Path.Combine(root, "agent.json")).SaveAsync(settings);
        await ServerMonitoringDispatcher.SaveSettingsAsync(root, connection);
        await new JobStore(Path.Combine(root, "jobs.json")).SaveAsync([job with { Name = "Local job must not run" }]);
        var before = await File.ReadAllBytesAsync(Path.Combine(root, "jobs.json"));
        var profile = new ManagedAgentProfile(root, settings, connection);
        var history = new BackupRunHistory(Path.Combine(root, "runs.json"));
        await history.SaveAsync(new(Guid.NewGuid(), job.Id, "Other enrollment", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow,
            BackupRunStatus.Failed, root) { Configuration = new(new ManagedAgentProfile(root, settings with { CredentialKey = "old-enrollment" }).ConnectionId, 99) });
        await Publish(new(agent.AgentId, 1, [job]));
        var executor = new BlockingArchive(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var host = new LocalAgentHost(root, executor, new Store(agent.Token)).RunAsync(pollInterval: TimeSpan.FromMilliseconds(25), cancellationToken: stop.Token);
        try
        {
            await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(1, (await history.ReadAsync()).Single(r => r.Status == BackupRunStatus.Running).Configuration!.Revision);
            await Publish(new(agent.AgentId, 2, [job with { Name = "Second revision", Enabled = false, ScheduleKind = ScheduleKind.Manual }]));
            await Wait(async () => (await fixture.Store.ConfigurationDecisionsAsync(agent.AgentId, default)).LastAccepted?.Revision == 2);
            executor.Release.TrySetResult();
            await Wait(async () => (await fixture.Store.RunsAsync(agent.AgentId, 0, default)).Runs.Count == 1);
            var report = Assert.Single((await fixture.Store.RunsAsync(agent.AgentId, 0, default)).Runs);
            Assert.Equal("First revision", report.JobName); Assert.Equal(1, report.ConfigurationRevision);
            Assert.DoesNotContain(profile.ConnectionId, JsonSerializer.Serialize(report));
            var snapshot = (await fixture.Store.MonitoringAsync(agent.AgentId, default))!.Snapshot;
            Assert.Equal("Managed", snapshot.ExecutionMode); Assert.True(snapshot.BackupExecutionAvailable);
            Assert.Equal("Second revision", Assert.Single(snapshot.Jobs).Name); Assert.Equal(2, snapshot.ConfigurationRevision);
            Assert.Contains(SPBackup.Server.AgentCapabilities.ManagedExecutionV1,
                snapshot.ConfigurationCapabilities!);
            using var page = await fixture.Administrator.GetAsync($"agents/{agent.AgentId}");
            Assert.Contains("Ревизия настроек этого запуска:", WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync()));
            Assert.False(SPBackup.Core.Monitoring.MonitoringValidation.Valid(
                ServerMonitoringDispatcher.ToReport((await history.ReadAsync()).Single(r => r.Status == BackupRunStatus.Succeeded)) with { ConfigurationRevision = 0 }));
        }
        finally { executor.Release.TrySetResult(); stop.Cancel(); Assert.Equal(0, await host.WaitAsync(TimeSpan.FromSeconds(8))); }
        Assert.False((await fixture.Store.AgentAsync(agent.AgentId, default))!.BackupExecutionAvailable);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(root, "jobs.json")));
        Assert.Single(await new BackupCatalog(profile.CatalogPath).ReadAsync());
        Assert.False(File.Exists(Path.Combine(root, "copies.json")));
    }
}
