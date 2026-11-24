using System.Security.Cryptography;

namespace SPBackup.Remote;

public sealed class RemoteStorageClient(ClientPairing pairing, bool allowLocalEndpoint = false)
{
    public async Task ProbeAsync(CancellationToken token = default) => Check(await RequestAsync(new("ping", Guid.NewGuid(), Guid.NewGuid()), token));
    public async Task<RemoteReply> PutAsync(Guid jobId, Guid objectId, string path, CancellationToken token = default, IProgress<long>? progress = null)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        var length = file.Length; var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token)); file.Position = 0;
        await using var stream = await SecureConnection.ConnectAsync(pairing, token, allowLocalEndpoint);
        await RemoteProtocol.WriteAsync(stream, new RemoteRequest("put", jobId, objectId, length, hash, Path.GetFileName(path)), token);
        var reply = await RemoteProtocol.ReadAsync<RemoteReply>(stream, token); Check(reply);
        if (reply.Code != "present")
        {
            var sent = await RemoteProtocol.CopyExactAsync(file, stream, length, token, progress);
            if (!sent.Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Source changed during transfer");
            await stream.FlushAsync(token); reply = await RemoteProtocol.ReadAsync<RemoteReply>(stream, token); Check(reply);
        }
        if (reply.Length != length || !reply.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Stored object differs");
        await RemoteProtocol.WriteAsync(stream, new RemoteReply(true, "received"), token);
        return reply;
    }
    public async Task<RemoteReply> StatAsync(Guid jobId, Guid objectId, CancellationToken token = default) => await RequestAsync(new("stat", jobId, objectId), token);
    public async Task DeleteAsync(Guid jobId, Guid objectId, long length, string hash, CancellationToken token = default) => Check(await RequestAsync(new("delete", jobId, objectId, length, hash), token));
    private async Task<RemoteReply> RequestAsync(RemoteRequest request, CancellationToken token)
    {
        await using var stream = await SecureConnection.ConnectAsync(pairing, token, allowLocalEndpoint);
        await RemoteProtocol.WriteAsync(stream, request, token); var reply = await RemoteProtocol.ReadAsync<RemoteReply>(stream, token); Check(reply);
        await RemoteProtocol.WriteAsync(stream, new RemoteReply(true, "received"), token); return reply;
    }
    public async Task GetAsync(Guid jobId, Guid objectId, long length, string hash, Stream target, CancellationToken token = default)
    {
        await using var stream = await SecureConnection.ConnectAsync(pairing, token, allowLocalEndpoint);
        await RemoteProtocol.WriteAsync(stream, new RemoteRequest("get", jobId, objectId), token);
        var reply = await RemoteProtocol.ReadAsync<RemoteReply>(stream, token); Check(reply);
        if (reply.Length != length || !reply.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Remote object changed");
        var received = await RemoteProtocol.CopyExactAsync(stream, target, length, token);
        if (!received.Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Downloaded object differs");
        await RemoteProtocol.WriteAsync(stream, new RemoteReply(true, "received"), token);
    }
    private static void Check(RemoteReply reply) { if (!reply.Success) throw new IOException("Remote storage: " + reply.Code); }
}
