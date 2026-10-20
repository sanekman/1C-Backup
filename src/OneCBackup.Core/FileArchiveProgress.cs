namespace OneCBackup.Core;

/// <summary>One byte-weighted phase for the whole source, including unchanged or skipped files.</summary>
internal sealed class FileArchiveProgress(long total, BackupStage stage = BackupStage.ArchivingFiles)
{
    private long completed;
    private long reported;
    internal void Report() => ReportWithinFile(0);
    internal void ReportWithinFile(long bytes)
    {
        reported = Math.Max(reported, Math.Min(total, completed + bytes));
        BackupProgressContext.Report(stage, reported, total);
    }
    internal async Task CopyStagedAsync(Stream input, Stream output, long plannedLength, CancellationToken token)
    {
        var buffer = new byte[81920]; long copied = 0; int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            copied += count;
            ReportWithinFile(plannedLength / 2 + (long)((double)copied / input.Length * (plannedLength - plannedLength / 2)));
        }
    }
    internal async Task CopyAsync(Stream input, Stream output, CancellationToken token, string? expectedHash = null)
    {
        if (expectedHash is null)
        { if (input.Length != 0) await BackupProgressContext.CopyAsync(input, output, stage, total, token, completed); return; }
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[81920]; long bytes = 0; int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            bytes += count; BackupProgressContext.Report(stage, completed + bytes, total);
        }
        if (!Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new FileBackupSourceException("Содержимое файла изменилось во время копирования.");
    }
    internal void Advance(long length)
    {
        if (length == 0) return;
        completed = checked(completed + length);
        Report();
    }
}
