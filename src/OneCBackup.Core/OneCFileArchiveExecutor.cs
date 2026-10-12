namespace OneCBackup.Core;

/// <summary>Archives a closed file database while holding an exclusive source handle.</summary>
public sealed class OneCFileArchiveExecutor(IArchivePasswordStore? passwords = null) : IBackupExecutor
{
    public async Task<string> CreateAsync(BackupJob job, string workDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (job.Kind != DatabaseKind.OneCFile || job.OneCMethod != OneCBackupMethod.FileArchive)
            throw new ArgumentException("Выберите архивацию файла базы 1С.", nameof(job));
        var password = await ArchivePasswords.ResolveAsync(job.ArchivePasswordKey, passwords, cancellationToken);
        var source = Path.GetFullPath(job.Source);
        var database = string.Equals(Path.GetFileName(source), "1Cv8.1CD", StringComparison.OrdinalIgnoreCase)
            ? source : Path.Combine(source, "1Cv8.1CD");
        if (!File.Exists(database)) throw new FileNotFoundException("Не найден файл базы 1Cv8.1CD.", database);
        if (File.GetAttributes(database).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Выберите настоящий файл базы, а не символическую ссылку.");

        FileStream input;
        try
        {
            input = new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (IOException ex)
        {
            throw new IOException("Не удалось получить эксклюзивный доступ к 1Cv8.1CD. Закройте сеансы 1С и проверьте доступ к файлу.", ex);
        }
        await using (input)
        {
            if (input.Length == 0) throw new InvalidDataException("Файл базы 1Cv8.1CD пуст.");
            var archive = Path.Combine(Path.GetFullPath(workDirectory),
                BackupFileName.Create(job, ".1CD.zip"));
            // Do not reopen the path: the exclusive handle remains held during packing and verification.
            return await new BackupArchiver().CreateZipAsync(input, "1Cv8.1CD", archive,
                cancellationToken: cancellationToken, password: password);
        }
    }
}
