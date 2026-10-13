namespace OneCBackup.Core;

/// <summary>One byte-weighted phase for the whole source, including unchanged or skipped files.</summary>
internal sealed class FileArchiveProgress(long total, BackupStage stage = BackupStage.ArchivingFiles)
{
    private long completed;
    internal void Report() => BackupProgressContext.Report(stage, completed, total);
    internal Task CopyAsync(Stream input, Stream output, CancellationToken token)
        => input.Length == 0 ? Task.CompletedTask : BackupProgressContext.CopyAsync(input, output, stage, total, token, completed);
    internal void Advance(long length)
    {
        if (length == 0) return;
        completed = checked(completed + length);
        Report();
    }
}
