using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Server.Tests;
public sealed class CentralHostTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private sealed class Credentials(string token) : IFtpCredentialStore
    { public Task<NetworkCredential> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(new NetworkCredential("agent", token)); }
    private sealed class Archive : IBackupExecutor
    {
        public int Count;
        public Task<string> CreateAsync(BackupJob job, string work, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Count); return new OneCFileArchiveExecutor().CreateAsync(job, work, cancellationToken); }
    }
    private static async Task Wait(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (!await condition()) await Task.Delay(100, timeout.Token);
    }
    [ServerFact]
    public async Task RealHostRemoteRunInvalidPlanOfflineRestartAndReportReplayKeepOneOutcomePerRun()
    {
        var agent = await fixture.EnrollAsync();
        var root = Path.Combine(fixture.Root, "central-host"); Directory.CreateDirectory(Path.Combine(root, "source"));
        await File.WriteAllTextAsync(Path.Combine(root, "source", "1Cv8.1CD"), "Fixture data — not a real 1C acceptance");
        var job = new BackupJob { Name = "Central host", Kind = DatabaseKind.OneCFile, OneCMethod = OneCBackupMethod.FileArchive,
            Source = Path.Combine(root, "source"), DestinationDirectory = Path.Combine(root, "copies"), Enabled = false, ScheduleKind = ScheduleKind.Manual };
        var settings = new AgentSettings { AgentId = agent.AgentId, Mode = AgentMode.Managed, ServerAddress = fixture.Address.ToString(), CredentialKey = "central-host", SyncIntervalSeconds = 15 };
        using var cert = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(fixture.Root, "certificate.pfx"), File.ReadAllText(Path.Combine(fixture.Root, "certificate-password.txt")));
        var connection = new ServerMonitoringSettings(settings.ServerAddress, agent.AgentId, settings.CredentialKey, Convert.ToHexString(SHA256.HashData(cert.RawData)));
        await new AgentSettingsStore(Path.Combine(root, "agent.json")).SaveAsync(settings);
        await ServerMonitoringDispatcher.SaveSettingsAsync(root, connection);
        async Task Publish(long revision, BackupJob selected) => await fixture.ExecuteAsync("UPDATE server_configurations SET revision=@revision,document=@document::jsonb WHERE agent_id=@agent",
            ("revision", revision), ("document", JsonSerializer.Serialize(new ServerConfiguration(agent.AgentId, revision, [selected]),
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } })), ("agent", agent.AgentId));
        await Publish(1, job);
        var executor = new Archive(); var history = new BackupRunHistory(Path.Combine(root, "runs.json"));
        using var firstStop = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var host = new LocalAgentHost(root, executor, new Credentials(agent.Token)).RunAsync(pollInterval: TimeSpan.FromMilliseconds(30), cancellationToken: firstStop.Token);
        try
        {
            await Wait(async () => (await fixture.Store.MonitoringAsync(agent.AgentId, default))?.Snapshot.ConfigurationCapabilities?.Contains(SPBackup.Server.AgentCapabilities.RemoteRunsV1) == true);
            var request = new SPBackup.Server.RemoteRunRequest(Guid.NewGuid(), job.Id, 1);
            using (var response = await fixture.Administrator.PostAsync($"api/v1/management/agents/{agent.AgentId}/commands", ServerFixture.Json(request)))
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            await Wait(async () => (await fixture.Store.RemoteRunsAsync(agent.AgentId, default)).Any(c => c.Command.CommandId == request.CommandId && c.Status == "Succeeded"));
            using (var duplicate = await fixture.Administrator.PostAsync($"api/v1/management/agents/{agent.AgentId}/commands", ServerFixture.Json(request)))
                Assert.Equal(HttpStatusCode.Accepted, duplicate.StatusCode);
            Assert.Equal(1, executor.Count); Assert.Single(await history.ReadAsync());
            await Publish(2, job with { Source = "" });
            await Wait(async () => (await fixture.Store.ConfigurationDecisionsAsync(agent.AgentId, default)).Current is { Revision: 2, Status: "Rejected" });
            var profile = new ManagedAgentProfile(root, settings, connection);
            Assert.Equal(1, await profile.CreateConfiguration().GetActiveRevisionAsync(settings));
            firstStop.Cancel(); Assert.Equal(0, await host.WaitAsync(TimeSpan.FromSeconds(10)));
            await fixture.App.StopAsync();
            try
            {
                using var offlineStop = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                var offline = new LocalAgentHost(root, executor, new Credentials(agent.Token)).RunAsync(pollInterval: TimeSpan.FromMilliseconds(30), cancellationToken: offlineStop.Token);
                try
                {
                    var control = new LocalAgentControlStore(root); LocalAgentState? state = null;
                    await Wait(async () => (state = await control.ReadAsync())?.CanRunManually == true);
                    var queue = new LocalManualRunQueue(root); var queued = await queue.EnqueueAsync(state!.SessionId, job.Id);
                    await Wait(async () => (await queue.ReadReceiptAsync(queued.RequestId))?.Phase == LocalManualRunPhase.Finished);
                    Assert.Equal(2, executor.Count); Assert.Equal(2, (await history.ReadAsync()).Count);
                }
                finally { offlineStop.Cancel(); Assert.Equal(0, await offline.WaitAsync(TimeSpan.FromSeconds(10))); }
            }
            finally { await fixture.App.StartAsync(); }
            using var finalStop = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            var final = new LocalAgentHost(root, executor, new Credentials(agent.Token)).RunAsync(pollInterval: TimeSpan.FromMilliseconds(30), cancellationToken: finalStop.Token);
            try
            {
                await Wait(async () => (await fixture.Store.RunsAsync(agent.AgentId, 0, default)).Runs.Count == 2);
                await Publish(3, job);
                await Wait(async () => (await fixture.Store.ConfigurationDecisionsAsync(agent.AgentId, default)).LastAccepted?.Revision == 3);
                Assert.Equal(2, executor.Count);
                Assert.Equal(2, (await fixture.Store.RunsAsync(agent.AgentId, 0, default)).Runs.Select(r => r.RunId).Distinct().Count());
                Assert.All((await fixture.Store.RunsAsync(agent.AgentId, 0, default)).Runs, r =>
                { Assert.Equal(1, r.ConfigurationRevision); Assert.Equal(ManagedJobBindings.Scope(job), r.BackupScope); });
            }
            finally { finalStop.Cancel(); Assert.Equal(0, await final.WaitAsync(TimeSpan.FromSeconds(10))); }
        }
        finally { firstStop.Cancel(); await host.WaitAsync(TimeSpan.FromSeconds(10)); }
    }
}
