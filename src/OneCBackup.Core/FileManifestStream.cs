namespace OneCBackup.Core;

/// <summary>Bounds uncompressed manifest I/O without buffering a second copy of the JSON.</summary>
internal sealed class FileManifestStream(Stream inner) : Stream
{
    private long count;
    private void Count(int bytes)
    {
        count = checked(count + bytes);
        if (count > FileBackupManifest.MaximumBytes)
            throw new InvalidDataException("Индекс файловой копии превышает 512 МБ.");
    }
    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => count;
    public override long Position { get => count; set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
    public override int Read(byte[] buffer, int offset, int length)
    { var read = inner.Read(buffer, offset, length); Count(read); return read; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    { var read = await inner.ReadAsync(buffer, token); Count(read); return read; }
    public override void Write(byte[] buffer, int offset, int length)
    { Count(length); inner.Write(buffer, offset, length); }
    public override void Write(ReadOnlySpan<byte> buffer)
    { Count(buffer.Length); inner.Write(buffer); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
    { Count(buffer.Length); await inner.WriteAsync(buffer, token); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    // The caller owns the underlying ZIP, hash, or file stream.
}
