using System.Diagnostics;

namespace OneCBackup.Core;

public enum BackupExportPhase { Preparing, Transferring, Verifying, Publishing, Completed }
public sealed record BackupExportProgress(BackupExportPhase Phase, long BytesCompleted, long TotalBytes);

internal sealed class BackupExportReporter(IProgress<BackupExportProgress>? progress, long total)
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private long previous = -100;
    private BackupExportPhase? phase;
    public void Report(BackupExportPhase current, long bytes, bool force = false)
    {
        if (progress is null) return;
        var now = clock.ElapsedMilliseconds;
        if (!force && phase == current && now - previous < 100) return;
        phase = current; previous = now;
        progress.Report(new(current, bytes, total));
    }
}

/// <summary>Observes writes without owning the caller's stream.</summary>
internal sealed class ExportWriteStream(Stream inner, BackupExportReporter reporter) : Stream
{
    private long written;
    public override bool CanRead => false;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    private void Written(int count) { written += count; reporter.Report(BackupExportPhase.Transferring, written); }
    public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); Written(count); }
    public override void Write(ReadOnlySpan<byte> buffer) { inner.Write(buffer); Written(buffer.Length); }
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    { await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken); Written(count); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { await inner.WriteAsync(buffer, cancellationToken); Written(buffer.Length); }
}
