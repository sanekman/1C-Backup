using System.Diagnostics;

namespace SPBackup.Core;

public enum FileRestorePhase { PreparingArchive, TransferringArchive, VerifyingArchive, ApplyingSnapshot, VerifyingFiles, Publishing, Completed }
public sealed record FileRestoreProgress(FileRestorePhase Phase, int ArchiveNumber, int Archives, long Completed, long Total);

internal sealed class FileRestoreReporter(IProgress<FileRestoreProgress>? progress)
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private long previous = -100;
    private FileRestorePhase? phase;
    private int archive;
    public void Report(FileRestorePhase current, int number, int archives, long completed, long total, bool force = false)
    {
        if (progress is null) return;
        var now = clock.ElapsedMilliseconds;
        if (!force && phase == current && archive == number && now - previous < 100 && completed != total) return;
        phase = current; archive = number; previous = now;
        progress.Report(new(current, number, archives, completed, total));
    }
    public IProgress<BackupExportProgress> ForArchive(int number, int archives) => new Forward(value =>
    {
        var mapped = value.Phase switch
        {
            BackupExportPhase.Preparing => FileRestorePhase.PreparingArchive,
            BackupExportPhase.Transferring => FileRestorePhase.TransferringArchive,
            BackupExportPhase.Verifying => FileRestorePhase.VerifyingArchive,
            _ => (FileRestorePhase?)null
        };
        if (mapped is { } current) Report(current, number, archives, value.BytesCompleted, value.TotalBytes);
    });
    private sealed class Forward(Action<BackupExportProgress> report) : IProgress<BackupExportProgress>
    { public void Report(BackupExportProgress value) => report(value); }
}
