using ICSharpCode.SharpZipLib.Zip;
using System.Security.Cryptography;

namespace OneCBackup.Core;

public sealed record ExtractedOneCFile(string Path, long Length, string Sha256);

/// <summary>Extracts a verified file-base archive into a new directory, without touching an existing base.</summary>
public sealed class OneCArchiveExtractor(IArchivePasswordStore? passwords = null)
{
    public async Task<ExtractedOneCFile> ExtractAsync(BackupRecord record, BackupJob job, string parentDirectory,
        CancellationToken cancellationToken = default, string? passwordOverride = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        job = BackupCopyVerifier.GetStorageJob(record, job);
        if (job.Kind != DatabaseKind.OneCFile || job.OneCMethod != OneCBackupMethod.FileArchive ||
            job.StorageKind is not (StorageKind.LocalDirectory or StorageKind.NetworkShare))
            throw new NotSupportedException("Извлечение поддерживает ZIP файла 1Cv8.1CD на локальном диске или в сетевой папке.");
        var source = BackupCopyVerifier.ValidateRecord(record, job);
        if (!System.IO.Path.IsPathFullyQualified(parentDirectory) || !Directory.Exists(parentDirectory))
            throw new DirectoryNotFoundException("Выберите существующий полный путь к родительскому каталогу.");
        if (File.GetAttributes(source).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Извлечение копии через символическую ссылку запрещено.");
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var sourceHash = await SHA256.HashDataAsync(input, cancellationToken);
        if (input.Length != record.Backup.Length || !string.Equals(Convert.ToHexString(sourceHash), record.Backup.Sha256,
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Размер или SHA256 архива не совпадает с журналом. Извлечение остановлено.");
        input.Position = 0;
        using var archive = new ZipFile(input) { IsStreamOwner = false };
        if (archive.Count != 1 || !string.Equals(archive[0].Name, "1Cv8.1CD", StringComparison.OrdinalIgnoreCase)
            || archive[0].Size <= 0)
            throw new InvalidDataException("Нужен архив с одним непустым файлом 1Cv8.1CD без вложенных каталогов.");
        var entry = archive[0];
        if (entry.IsCrypted)
        {
            var password = passwordOverride ?? await ArchivePasswords.ResolveAsync(record.Backup.ArchivePasswordKey, passwords, cancellationToken);
            if (password is null) throw new InvalidDataException("Для извлечения зашифрованного архива введите пароль.");
            ArchivePasswords.Validate(password);
            archive.Password = password;
        }
        var destination = System.IO.Path.Combine(System.IO.Path.GetFullPath(parentDirectory), "1C-Restored-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);
        var temporary = System.IO.Path.Combine(destination, "1Cv8.1CD.partial");
        var final = System.IO.Path.Combine(destination, "1Cv8.1CD");
        try
        {
            byte[] fileHash;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                81920, FileOptions.Asynchronous))
            {
                await using var content = archive.GetInputStream(entry);
                await content.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
                if (output.Length != entry.Size) throw new InvalidDataException("Размер извлечённого файла не совпадает с архивом.");
                output.Position = 0;
                fileHash = await SHA256.HashDataAsync(output, cancellationToken);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, final, overwrite: false);
            return new ExtractedOneCFile(final, entry.Size, Convert.ToHexString(fileHash));
        }
        catch (Exception failure)
        {
            // Only remove this operation's partial file and an empty directory; never recurse.
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                if (!Directory.EnumerateFileSystemEntries(destination).Any()) Directory.Delete(destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (failure is ZipException or CryptographicException)
                throw new InvalidDataException("Не удалось расшифровать ZIP: неверный пароль или повреждённый архив.", failure);
            throw;
        }
    }
}
