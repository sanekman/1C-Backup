using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using OneCBackup.Core;
using OneCBackup.Remote;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class RemoteGatewayTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OneCBackup-remote-" + Guid.NewGuid());
    private readonly CancellationTokenSource shutdown = new();
    private Microsoft.AspNetCore.Builder.WebApplication? hub;
    private Task? gateway;
    private ClientPairing pairing = null!;
    private GatewayOptions options = null!;
    private string Data => Path.Combine(directory, "data");
    public async Task InitializeAsync()
    {
        RemoteConfiguration.Initialize(directory, "wss://relay.example.com");
        var config = await RemoteConfiguration.ReadAsync<HubOptions>(Path.Combine(directory, "hub.json"));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        config = config with { Port = port };
        hub = RemoteHub.Build(config); await hub.StartAsync();
        pairing = (await RemoteConfiguration.ReadAsync<ClientPairing>(Path.Combine(directory, "client-pairing.json"))) with { Endpoint = "ws://127.0.0.1:" + port };
        options = new(pairing.GatewayId, pairing.Endpoint, Path.Combine(directory, "access-token.txt"), Path.Combine(directory, "gateway.pfx"),
            Path.Combine(directory, "certificate-password.txt"), Path.Combine(directory, "agent.pem"), Data, 4 * 1024 * 1024);
        gateway = new StorageGateway(options, allowLocalEndpoint: true).RunAsync(shutdown.Token);
        using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            if (gateway.IsCompleted) await gateway;
            try { await new RemoteStorageClient(pairing, true).ProbeAsync(ready.Token); break; }
            catch (HttpRequestException) { await Task.Delay(50, ready.Token); }
        }
    }
    public async Task DisposeAsync()
    {
        shutdown.Cancel(); if (gateway is not null) { try { await gateway.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { } }
        if (hub is not null) await hub.DisposeAsync(); shutdown.Dispose(); Directory.Delete(directory, true);
    }
    [Fact]
    public async Task MutualTlsUploadVerifyDownloadAndOwnedDeleteWorkThroughRelay()
    {
        var bytes = RandomNumberGenerator.GetBytes(256 * 1024); var source = Path.Combine(directory, "copy.zip"); await File.WriteAllBytesAsync(source, bytes);
        var client = new RemoteStorageClient(pairing, true); var job = Guid.NewGuid(); var id = Guid.NewGuid();
        var stored = await client.PutAsync(job, id, source);
        Assert.Equal(bytes.Length, stored.Length); Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), stored.Sha256);
        Assert.Single(Directory.EnumerateFiles(Data, "*.bin", SearchOption.AllDirectories));
        var metadata = Assert.Single(Directory.EnumerateFiles(Data, "*.json", SearchOption.AllDirectories));
        Assert.Contains("copy.zip", await File.ReadAllTextAsync(metadata));
        Assert.Empty(Directory.EnumerateFiles(Data, "*.part", SearchOption.AllDirectories));
        Assert.Equal(stored.Sha256, (await client.StatAsync(job, id)).Sha256);
        using var downloaded = new MemoryStream(); await client.GetAsync(job, id, stored.Length, stored.Sha256, downloaded);
        Assert.Equal(bytes, downloaded.ToArray());
        var repeated = await client.PutAsync(job, id, source); Assert.Equal("present", repeated.Code);
        await Assert.ThrowsAsync<IOException>(() => client.DeleteAsync(job, id, stored.Length, new string('0', 64)));
        Assert.Single(Directory.EnumerateFiles(Data, "*.bin", SearchOption.AllDirectories));
        await client.DeleteAsync(job, id, stored.Length, stored.Sha256);
        Assert.Empty(Directory.EnumerateFiles(Data, "*.bin", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(Data, "*.json", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task UnknownTokenAndWrongPeerCertificateCannotAccessStorage()
    {
        var denied = await Assert.ThrowsAsync<HttpRequestException>(() => new RemoteStorageClient(pairing with { AccessToken = new string('0', 64) }, true).ProbeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var other = Path.Combine(directory, "other"); RemoteConfiguration.Initialize(other, "wss://other.example.com");
        var foreign = await RemoteConfiguration.ReadAsync<ClientPairing>(Path.Combine(other, "client-pairing.json"));
        await Assert.ThrowsAnyAsync<AuthenticationException>(() => new RemoteStorageClient(pairing with { PeerCertificate = foreign.PeerCertificate }, true).ProbeAsync());
        Assert.Empty(Directory.EnumerateFiles(Data, "*.bin", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task QuotaRejectsNewCopyWithoutLosingPreviousObject()
    {
        var client = new RemoteStorageClient(pairing, true); var source = Path.Combine(directory, "large.zip");
        await File.WriteAllBytesAsync(source, new byte[3 * 1024 * 1024]); var job = Guid.NewGuid(); var id = Guid.NewGuid();
        var stored = await client.PutAsync(job, id, source);
        var error = await Assert.ThrowsAsync<IOException>(() => client.PutAsync(job, Guid.NewGuid(), source));
        Assert.Contains("quota_exceeded", error.Message); Assert.Equal(stored.Sha256, (await client.StatAsync(job, id)).Sha256);
        Assert.Single(Directory.EnumerateFiles(Data, "*.bin", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task CoreAdapterKeepsLocationScopedToGatewayAndJob()
    {
        var source = Path.Combine(directory, "copy.zip"); await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(4096));
        var settings = new RemoteGatewaySettings { GatewayId = pairing.GatewayId, Endpoint = "wss://relay.example.com", CredentialKey = Guid.NewGuid().ToString("N"), PeerCertificate = pairing.PeerCertificate };
        var job = new BackupJob { Source = "fixture", Name = "test", StorageKind = StorageKind.RemoteGateway, DestinationDirectory = "remote", RemoteGateway = settings,
            ArchiveCopies = true, ArchivePasswordKey = "fixture" };
        Assert.Empty(job.Validate()); Assert.NotEmpty((job with { ArchivePasswordKey = null }).Validate());
        var storage = new RemoteGatewayBackupStorage(new RemoteStorageClient(pairing, true));
        await storage.PrepareAsync(job); var stored = await storage.StoreAsync(source, job); await storage.VerifyAsync(stored, job);
        await Assert.ThrowsAsync<InvalidDataException>(() => storage.DeleteAsync(stored, job with { Id = Guid.NewGuid() }));
        await Assert.ThrowsAsync<InvalidDataException>(() => storage.VerifyAsync(stored, job with { RemoteGateway = settings with { GatewayId = Guid.NewGuid() } }));
        using var copy = new MemoryStream(); await storage.CopyToAsync(stored, job, copy); Assert.Equal(await File.ReadAllBytesAsync(source), copy.ToArray());
        await storage.DeleteAsync(stored, job);
    }
    [Fact]
    public async Task InterruptedUploadDoesNotPublishPartialObjectAndOtherRequestsContinue()
    {
        var job = Guid.NewGuid(); var id = Guid.NewGuid();
        await using (var stream = await SecureConnection.ConnectAsync(pairing, CancellationToken.None, true))
        {
            await RemoteProtocol.WriteAsync(stream, new RemoteRequest("put", job, id, 1024, new string('0', 64)), CancellationToken.None);
            Assert.Equal("ready", (await RemoteProtocol.ReadAsync<RemoteReply>(stream, CancellationToken.None)).Code);
            await stream.WriteAsync(new byte[10]);
        }
        var source = Path.Combine(directory, "after-cancel.zip"); await File.WriteAllBytesAsync(source, new byte[4096]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = new RemoteStorageClient(pairing, true);
        await client.PutAsync(job, Guid.NewGuid(), source, timeout.Token);
        Assert.False(File.Exists(Path.Combine(Data, job.ToString("N"), id.ToString("N") + ".bin")));
        Assert.Empty(Directory.EnumerateFiles(Data, "*.part", SearchOption.AllDirectories));
    }
}
