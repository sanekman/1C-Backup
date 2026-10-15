using System.Security.Cryptography;

namespace OneCBackup.Core;

public sealed record ExportedBackup(string Path, long Length, string Sha256);

/// <summary>Copies a registered backup to a new user-selected file, verifies bytes, then publishes atomically.</summary>
public sealed class BackupCopyExporter(IBackupStorage storage)
{
    public async Task<ExportedBackup> ExportAsync(BackupRecord record, BackupJob job, string destinationPath,
        CancellationToken cancellationToken = default, IProgress<BackupExportProgress>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (record.Id == Guid.Empty || record.Backup is null || record.Backup.Length < 0 || record.Backup.Sha256 is null
            || record.Backup.Sha256.Length != 64 || record.Backup.Sha256.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("Некорректная запись копии.");
        BackupCopyVerifier.ValidateRecord(record, job);
        var target = BackupCopyVerifier.GetStorageJob(record, job);
        if (string.IsNullOrWhiteSpace(destinationPath) || !Path.IsPathFullyQualified(destinationPath))
            throw new ArgumentException("Укажите полный путь нового файла.", nameof(destinationPath));
        var destination = Path.GetFullPath(destinationPath);
        var parent = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Выбранная папка не существует.");
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("Файл или папка уже существует. Выберите другое имя: сохранённые файлы не перезаписываются.");
        var temporary = Path.Combine(parent, ".onec-export-" + Guid.NewGuid().ToString("N") + ".partial");
        var ownsTemporary = false;
        var reporter = new BackupExportReporter(progress, record.Backup.Length);
        try
        {
            reporter.Report(BackupExportPhase.Preparing, 0, force: true);
            await storage.PrepareAsync(target, cancellationToken);
            string hash;
            long length;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                81920, FileOptions.Asynchronous))
            {
                ownsTemporary = true;
                reporter.Report(BackupExportPhase.Transferring, 0, force: true);
                using (var observed = new ExportWriteStream(output, reporter))
                    await storage.CopyToAsync(record.Backup, target, observed, cancellationToken);
                await output.FlushAsync(cancellationToken);
                length = output.Length;
                reporter.Report(BackupExportPhase.Transferring, length, force: true);
                output.Position = 0;
                reporter.Report(BackupExportPhase.Verifying, 0, force: true);
                using var checksum = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long read = 0;
                int count;
                while ((count = await output.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    checksum.AppendData(buffer, 0, count);
                    read += count;
                    reporter.Report(BackupExportPhase.Verifying, read);
                }
                hash = Convert.ToHexString(checksum.GetHashAndReset());
                reporter.Report(BackupExportPhase.Verifying, read, force: true);
                if (length != record.Backup.Length || !hash.Equals(record.Backup.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Размер или SHA-256 не совпадает с каталогом. Копия не сохранена: исходный файл изменён или повреждён.");
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            reporter.Report(BackupExportPhase.Publishing, length, force: true);
            File.Move(temporary, destination, overwrite: false);
            reporter.Report(BackupExportPhase.Completed, length, force: true);
            return new(destination, length, hash);
        }
        finally { if (ownsTemporary && File.Exists(temporary)) File.Delete(temporary); }
    }
}
