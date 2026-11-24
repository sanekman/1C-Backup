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
    private sealed class CountingArchive : IBackupExecutor
    {
        public int Count;
        public Task<string> CreateAsync(BackupJob job, string work, CancellationToken token = default)
        { Interlocked.Increment(ref Count); return new OneCFileArchiveExecutor().CreateAsync(job, work, token); }
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

    [ServerFact]
    public async Task ManagedHostDeliversOneArchivePerRunKeepsIndependentLimitsAndReportsAPartialFailure()
    {
        var agent = await fixture.EnrollAsync(); var root = Path.Combine(fixture.Root, "multiple-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "1Cv8.1CD"), "closed file backup fixture");
        var blocked = Path.Combine(root, "blocked"); await File.WriteAllTextAsync(blocked, "keep this file");
        var secondary = Path.Combine(root, "second");
        var job = new BackupJob { Name = "Managed independent destinations", Kind = DatabaseKind.OneCFile,
            OneCMethod = OneCBackupMethod.FileArchive, Source = source, DestinationDirectory = Path.Combine(root, "first"),
            Enabled = false, ScheduleKind = ScheduleKind.Manual, KeepLast = 1,
            AdditionalDestinations = [new() { DestinationDirectory = blocked }, new() { DestinationDirectory = secondary, KeepLast = 2 }] };
        if (OperatingSystem.IsWindows())
        {
            // Windows executes the exact limited management API document. Linux exercises
            // the same host/report path with native paths; Windows path validation is tested separately.
            var published = new SPBackup.Server.ManagedFileJob { Id = job.Id, Name = job.Name, Kind = "OneCFile", Source = job.Source,
                DestinationDirectory = job.DestinationDirectory, KeepLast = 1, ScheduleKind = "Manual", Enabled = false,
                AdditionalDestinations = [new() { StorageKind = "LocalDirectory", DestinationDirectory = blocked },
                    new() { StorageKind = "LocalDirectory", DestinationDirectory = secondary, KeepLast = 2 }] };
            using var request = new HttpRequestMessage(HttpMethod.Put, $"api/v1/management/agents/{agent.AgentId}/configuration")
                { Content = ServerFixture.Json(new SPBackup.Server.PublishFileConfiguration(agent.AgentId, 1, [published])) };
            request.Headers.IfMatch.Add(new("\"1\"")); using var response = await fixture.Administrator.SendAsync(request); response.EnsureSuccessStatusCode();
        }
        else await Publish(new(agent.AgentId, 2, [job]));
        var profileRoot = Path.Combine(root, "profile");
        var settings = new AgentSettings { AgentId = agent.AgentId, Mode = AgentMode.Managed,
            ServerAddress = fixture.Address.ToString(), CredentialKey = "multiple-enrollment", SyncIntervalSeconds = 15 };
        using var cert = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(fixture.Root, "certificate.pfx"),
            File.ReadAllText(Path.Combine(fixture.Root, "certificate-password.txt")));
        var connection = new ServerMonitoringSettings(settings.ServerAddress, agent.AgentId, settings.CredentialKey,
            Convert.ToHexString(SHA256.HashData(cert.RawData)));
        await new AgentSettingsStore(Path.Combine(profileRoot, "agent.json")).SaveAsync(settings);
        await ServerMonitoringDispatcher.SaveSettingsAsync(profileRoot, connection);
        var profile = new ManagedAgentProfile(profileRoot, settings, connection);
        var history = new BackupRunHistory(Path.Combine(profileRoot, "runs.json"));
        var executor = new CountingArchive(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var host = new LocalAgentHost(profileRoot, executor, new Store(agent.Token)).RunAsync(pollInterval: TimeSpan.FromMilliseconds(25), cancellationToken: stop.Token);
        try
        {
            var control = new LocalAgentControlStore(profileRoot);
            await Wait(async () => (await control.ReadAsync()) is { CanRunManually: true, ConfigurationRevision: 2 });
            for (var index = 0; index < 3; index++)
            {
                var state = (await control.ReadAsync())!;
                await new LocalManualRunQueue(profileRoot).EnqueueAsync(state.SessionId, job.Id);
                await Wait(async () => (await history.ReadAsync()).Count(r => r.Status == BackupRunStatus.PartiallySucceeded) == index + 1);
            }
            Assert.Equal(3, executor.Count);
            var runs = await history.ReadAsync(); Assert.Equal(3, runs.Count);
            foreach (var run in runs)
            {
                Assert.Equal(2, run.Configuration!.Revision); Assert.Equal(3, run.Destinations!.Count);
                Assert.NotNull(run.Destinations[1].Error); Assert.Null(run.Destinations[1].Record);
                Assert.Equal(run.Destinations[0].Record!.Backup.Sha256, run.Destinations[2].Record!.Backup.Sha256);
            }
            var records = await new BackupCatalog(profile.CatalogPath).ReadAsync(); Assert.Equal(3, records.Count);
            Assert.Single(records, r => Path.GetDirectoryName(r.Backup.Location) == job.DestinationDirectory);
            Assert.Equal(2, records.Count(r => Path.GetDirectoryName(r.Backup.Location) == secondary));
            foreach (var record in records)
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(record.Backup.Location);
                using var reader = new StreamReader(zip.GetEntry("1Cv8.1CD")!.Open());
                Assert.Equal("closed file backup fixture", await reader.ReadToEndAsync());
            }
            Assert.Equal("keep this file", await File.ReadAllTextAsync(blocked));
            await Wait(async () => (await fixture.Store.RunsAsync(agent.AgentId, 0, default)).Runs.Count == 3);
            var reported = (await fixture.Store.RunsAsync(agent.AgentId, 0, default)).Runs;
            Assert.All(reported, r => {
                Assert.Equal("PartiallySucceeded", r.Status); Assert.Equal(2, r.ConfigurationRevision);
                Assert.Equal(3, r.Destinations.Count); Assert.False(r.Destinations.Single(d => d.Index == 1).Succeeded);
                Assert.Equal(2, r.Destinations.Count(d => d.Succeeded)); });
            Assert.Equal(3, Assert.Single((await fixture.Store.MonitoringAsync(agent.AgentId, default))!.Snapshot.Jobs).DestinationCount);
        }
        finally { stop.Cancel(); Assert.Equal(0, await host.WaitAsync(TimeSpan.FromSeconds(8))); }
    }
}
