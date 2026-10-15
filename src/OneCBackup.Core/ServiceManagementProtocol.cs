using System.Buffers.Binary;
using System.Text.Json;

namespace OneCBackup.Core;

public sealed record ServiceManagementRequest(string Action, string Executable, string Directory,
    string Account, string OwnerSid, string? Password);
public sealed record ServiceManagementResult(bool Success, string Message);

/// <summary>Length-bounded in-memory messages; passwords never go into arguments or temporary files.</summary>
public static class ServiceManagementProtocol
{
    public const int MaximumMessageBytes = 16384;
    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        try
        {
            if (bytes.Length > MaximumMessageBytes) throw new InvalidDataException("Сообщение службы слишком большое.");
            var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
            await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumMessageBytes) throw new InvalidDataException("Некорректная длина сообщения службы.");
        var bytes = new byte[length];
        try { await stream.ReadExactlyAsync(bytes, token); return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Пустое сообщение службы."); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }
}
