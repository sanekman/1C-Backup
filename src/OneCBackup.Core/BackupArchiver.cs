using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace OneCBackup.Core;

/// <summary>Packages a completed, stable backup file; never archives a live database.</summary>
public sealed class BackupArchiver
{
    public Task<string> PrepareAsync(BackupJob job, string completedBackupPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();
        var source = Path.GetFullPath(completedBackupPath);
        if (!File.Exists(source)) throw new FileNotFoundException("Файл резервной копии не найден.", source);
        if (job.ArchivePasswordKey is not null)
            throw new InvalidOperationException("Для зашифрованных заданий используйте BackupDelivery с хранилищем паролей.");
        if (!job.ShouldArchiveCopy) return Task.FromResult(source);
        if (job.StorageKind == StorageKind.Ftp)
            throw new ArgumentException("Для FTP используйте BackupDelivery с локальным рабочим каталогом.", nameof(job));
        if (string.IsNullOrWhiteSpace(job.DestinationDirectory))
            throw new ArgumentException("Укажите каталог резервных копий.", nameof(job));
        return CreateZipAsync(source, Path.Combine(job.DestinationDirectory, Path.GetFileName(source) + ".zip"),
            cancellationToken: cancellationToken);
    }

    /// <summary>Creates and verifies ZIP before publishing it. Existing files are never replaced.</summary>
    public async Task<string> CreateZipAsync(string sourcePath, string archivePath,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default, string? password = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(archivePath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(source, destination, comparison))
            throw new ArgumentException("Архив и исходная копия должны иметь разные пути.", nameof(archivePath));
        if (File.Exists(destination)) throw new IOException("Архив с таким именем уже существует.");

        // On Windows the sharing mode also prevents writers from changing the source.
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await CreateZipAsync(input, Path.GetFileName(source), destination, progress, cancellationToken, password);
    }

    /// <summary>The caller retains ownership and its file lock until this method completes.</summary>
    public async Task<string> CreateZipAsync(Stream input, string entryName, string archivePath,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default, string? password = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!input.CanRead) throw new ArgumentException("Поток должен быть доступен для чтения.", nameof(input));
        if (string.IsNullOrWhiteSpace(entryName) || entryName is "." or ".." || entryName.Contains('/') || entryName.Contains('\\'))
            throw new ArgumentException("Укажите имя файла без пути.", nameof(entryName));
        var destination = Path.GetFullPath(archivePath);
        if (File.Exists(destination)) throw new IOException("Архив с таким именем уже существует.");
        var total = input.CanSeek ? input.Length - input.Position : 0;
        BackupProgressContext.Report(BackupStage.Archiving, 0, total);
        progress = BackupProgressContext.Bytes(BackupStage.Archiving, total, progress);
        if (password is not null)
            return await EncryptedZipArchiver.CreateAsync(input, entryName, destination, password, progress, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        long bytesCopied = 0;
        byte[] digest;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
                {
                    var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    await using var entryStream = entry.Open();
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
                    {
                        await entryStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        hash.AppendData(buffer, 0, read);
                        bytesCopied += read;
                        progress?.Report(bytesCopied);
                    }
                }
                digest = hash.GetHashAndReset();
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }

            BackupProgressContext.Report(BackupStage.Verifying);
            // Read the whole extracted entry, rather than just checking ZIP metadata.
            using (var archive = ZipFile.OpenRead(temporary))
            {
                if (archive.Entries.Count != 1 || archive.Entries[0].FullName != entryName
                    || archive.Entries[0].Length != bytesCopied)
                    throw new InvalidDataException("Структура ZIP не соответствует резервной копии.");
                await using var extracted = archive.Entries[0].Open();
                var verifiedDigest = await SHA256.HashDataAsync(extracted, cancellationToken);
                if (!CryptographicOperations.FixedTimeEquals(digest, verifiedDigest))
                    throw new InvalidDataException("Проверка содержимого ZIP не пройдена.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
