using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace OneCBackup.Remote;

public sealed record RemoteRequest(string Method, Guid JobId, Guid ObjectId, long Length = 0, string Sha256 = "", string OriginalFileName = "");
public sealed record RemoteReply(bool Success, string Code = "ok", long Length = 0, string Sha256 = "");
public static class RemoteProtocol
{
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value); if (json.Length > 8192) throw new InvalidDataException("Message too large");
        var prefix = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(prefix, json.Length);
        await stream.WriteAsync(prefix, token); await stream.WriteAsync(json, token); await stream.FlushAsync(token);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, token); var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length is < 1 or > 8192) throw new InvalidDataException("Invalid message size");
        var json = new byte[length]; await stream.ReadExactlyAsync(json, token);
        return JsonSerializer.Deserialize<T>(json) ?? throw new InvalidDataException("Empty message");
    }
    public static async Task<string> CopyExactAsync(Stream source, Stream target, long length, CancellationToken token, IProgress<long>? progress = null)
    {
        if (length < 0) throw new InvalidDataException("Invalid length");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[128 * 1024]; long copied = 0;
        while (copied < length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - copied)), token);
            if (read == 0) throw new EndOfStreamException(); hash.AppendData(buffer, 0, read); await target.WriteAsync(buffer.AsMemory(0, read), token);
            copied += read; progress?.Report(copied);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
