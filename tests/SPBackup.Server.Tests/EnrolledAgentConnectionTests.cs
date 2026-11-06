using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Server.Tests;

public sealed class EnrolledAgentConnectionTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private sealed class Store(string key, string token) : IFtpCredentialStore
    {
        public string Value = token;
        public Task<NetworkCredential> GetAsync(string requested, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Assert.Equal(key, requested);
            return Task.FromResult(new NetworkCredential("agent", Value));
        }
    }

    [ServerFact]
    public async Task EnrolledPinnedConnectionSynchronizesReceiptsAndHeartbeatWithoutPersistingToken()
    {
        var agent = await fixture.EnrollAsync();
        using var cert = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(fixture.Root, "certificate.pfx"),
            File.ReadAllText(Path.Combine(fixture.Root, "certificate-password.txt")));
        var connection = new ServerMonitoringSettings(fixture.Address.ToString(), agent.AgentId, "enrolled-agent",
            Convert.ToHexString(SHA256.HashData(cert.RawData)));
        var settings = new AgentSettings { AgentId = agent.AgentId, Mode = AgentMode.Managed,
            ServerAddress = connection.Address, CredentialKey = connection.CredentialKey };
        var store = new Store(connection.CredentialKey, agent.Token);
        var cachePath = Path.Combine(fixture.Root, Guid.NewGuid() + ".json");
        var local = new JobStore(Path.Combine(fixture.Root, Guid.NewGuid() + ".json"));
        using (var api = new AgentApiClient(connection, store))
        {
            var configuration = new AgentConfiguration(local, new(cachePath), api);
            var sync = await configuration.SynchronizeAsync(settings, DateTimeOffset.UtcNow);
            Assert.True(sync.Succeeded); Assert.False(sync.ReceiptPending); Assert.Equal(1, sync.Revision);
            await api.SendHeartbeatAsync(settings, new(agent.AgentId, "connection-test", false, sync.Revision, DateTimeOffset.UtcNow));
            Assert.Equal(1, (await fixture.Store.AgentAsync(agent.AgentId, default))!.ReportedRevision);
            var state = await fixture.Store.ConfigurationDecisionsAsync(agent.AgentId, default);
            Assert.Equal("Accepted", state.Current!.Status); Assert.Equal(1, state.LastAccepted!.Revision);
        }
        // Recreating the adapter uses the same enrollment, protected store and durable configuration.
        using var restarted = new AgentApiClient(connection, store);
        var restored = new AgentConfiguration(local, new(cachePath), restarted);
        Assert.True((await restored.SynchronizeAsync(settings, DateTimeOffset.UtcNow)).Succeeded);
        var before = await File.ReadAllBytesAsync(cachePath);
        Assert.DoesNotContain(agent.Token, await File.ReadAllTextAsync(cachePath));
        store.Value = "invalid-token";
        var failed = await restored.SynchronizeAsync(settings, DateTimeOffset.UtcNow);
        Assert.False(failed.Succeeded); Assert.Equal(1, failed.Revision); Assert.IsType<HttpRequestException>(failed.Error);
        Assert.Equal(before, await File.ReadAllBytesAsync(cachePath));
        store.Value = agent.Token;
        Assert.True((await restored.SynchronizeAsync(settings, DateTimeOffset.UtcNow)).Succeeded);
        Assert.Equal(1, await restored.GetActiveRevisionAsync(settings));
    }
}
