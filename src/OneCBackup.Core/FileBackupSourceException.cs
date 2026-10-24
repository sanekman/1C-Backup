namespace OneCBackup.Core;

/// <summary>Only failures while reading the source, never failures writing a backup.</summary>
internal sealed class FileBackupSourceException(string message, Exception? inner = null) : IOException(message, inner)
{
    internal static bool IsReadFailure(Exception error) => error is UnauthorizedAccessException or System.Security.SecurityException
        || error is IOException;

    internal static T Read<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception error) when (IsReadFailure(error) && error is not FileBackupSourceException)
        { throw new FileBackupSourceException("Не удалось прочитать исходный файл или папку.", error); }
    }
}

internal sealed class FileBackupSourceStream(Stream input, string relativePath, long expectedLength) : Stream
{
    private long read;
    public override bool CanRead => input.CanRead;
    public override bool CanSeek => input.CanSeek;
    public override bool CanWrite => false;
    public override long Length => FileBackupSourceException.Read(() => input.Length);
    public override long Position
    {
        get => FileBackupSourceException.Read(() => input.Position);
        set { FileBackupSourceException.Read(() => input.Position = value); read = value; }
    }
    private int Check(int count)
    {
        read += count;
        if (read > expectedLength || count == 0 && read != expectedLength)
            throw new FileBackupSourceException("Файл изменился во время чтения: " + relativePath);
        return count;
    }
    public override int Read(byte[] buffer, int offset, int count)
        => count == 0 ? 0 : Check(FileBackupSourceException.Read(() => input.Read(buffer, offset, count)));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        if (buffer.Length == 0) { token.ThrowIfCancellationRequested(); return 0; }
        int count;
        try { count = await input.ReadAsync(buffer, token); }
        catch (Exception error) when (FileBackupSourceException.IsReadFailure(error) && error is not FileBackupSourceException)
        { throw new FileBackupSourceException("Не удалось прочитать файл: " + relativePath, error); }
        return Check(count);
    }
    public override long Seek(long offset, SeekOrigin origin)
    { var position = FileBackupSourceException.Read(() => input.Seek(offset, origin)); read = position; return position; }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
    public override ValueTask DisposeAsync() => input.DisposeAsync();
}
