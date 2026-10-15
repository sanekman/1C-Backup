using System.Buffers.Binary;
using OneCBackup.Core;
using OneCBackup.Remote;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class RemoteProtocolSafetyTests
{
    [Theory]
    [InlineData("ws://relay.example.com", false)]
    [InlineData("ws://relay.example.com", true)]
    [InlineData("wss://user:secret@relay.example.com", false)]
    [InlineData("wss://relay.example.com/path", false)]
    [InlineData("wss://relay.example.com/?secret=1", false)]
    public void InsecureOrSecretBearingEndpointsAreRejected(string endpoint, bool local) => Assert.Throws<InvalidDataException>(() => RemoteConfiguration.CheckEndpoint(endpoint, local));
    [Fact]
    public void LoopbackExceptionStillRequiresExplicitLocalMode()
    {
        Assert.Throws<InvalidDataException>(() => RemoteConfiguration.CheckEndpoint("ws://127.0.0.1:7087"));
        RemoteConfiguration.CheckEndpoint("ws://127.0.0.1:7087", true);
        RemoteConfiguration.CheckEndpoint("wss://relay.example.com");
    }
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(8193)]
    public async Task InvalidFramesAreRejectedBeforeAllocatingPayload(int size)
    {
        var prefix = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(prefix, size);
        await Assert.ThrowsAsync<InvalidDataException>(() => RemoteProtocol.ReadAsync<RemoteRequest>(new MemoryStream(prefix), CancellationToken.None));
    }
    [Fact]
    public async Task InterruptedCopyNeverReportsTheExpectedSizeOrHash()
    {
        await Assert.ThrowsAsync<EndOfStreamException>(() => RemoteProtocol.CopyExactAsync(new MemoryStream([1, 2, 3]), Stream.Null, 4, CancellationToken.None));
    }
    [Fact]
    public void RemoteLocationCannotRedirectDeletionToAnotherJobOrGateway()
    {
        var gateway = Guid.NewGuid(); var job = new BackupJob { RemoteGateway = new() { GatewayId = gateway } };
        var copy = new StoredBackup($"onec-remote://{gateway:N}/{job.Id:N}/{Guid.NewGuid():N}", 1, new string('0', 64));
        RemoteGatewaySettings.ObjectId(job, copy);
        Assert.Throws<InvalidDataException>(() => RemoteGatewaySettings.ObjectId(job, copy with { Location = copy.Location.Replace(gateway.ToString("N"), Guid.NewGuid().ToString("N")) }));
        Assert.Throws<InvalidDataException>(() => RemoteGatewaySettings.ObjectId(job, copy with { Location = copy.Location + "?other=1" }));
        Assert.Throws<InvalidDataException>(() => RemoteGatewaySettings.ObjectId(job, copy with { Location = copy.Location.Replace("/" + job.Id.ToString("N"), "/..") }));
    }
}
