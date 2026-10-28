namespace SPBackup.Core;

/// <summary>Archives a file database; shared reading requires an explicit job opt-in.</summary>
public sealed class OneCFileArchiveExecutor(IArchivePasswordStore? passwords = null) : IBackupExecutor
{
    public const string OpenDatabaseWarning = "Разрешено копирование открытой базы. Если 1С изменяет данные во время архивации, копия может оказаться повреждённой. Проверка ZIP не проверяет целостность базы. Для надёжной копии закройте базу у всех пользователей.";

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

        var input = OpenDatabase(database, job.AllowOpenOneCDatabase, out var shared);
        if (shared) BackupWarningContext.Report(OpenDatabaseWarning);
        await using (input)
        {
            if (input.Length == 0) throw new InvalidDataException("Файл базы 1Cv8.1CD пуст.");
            var archive = Path.Combine(Path.GetFullPath(workDirectory),
                BackupFileName.Create(job, ".1CD.zip"));
            // Do not reopen the path: retain the selected sharing mode through packing and verification.
            return await new BackupArchiver().CreateZipAsync(input, "1Cv8.1CD", archive,
                cancellationToken: cancellationToken, password: password);
        }
    }

    internal static FileStream OpenDatabase(string database, bool allowOpen, out bool shared)
    {
        shared = false;
        try { return Open(FileShare.None); }
        catch (IOException ex) when (!allowOpen)
        { throw new IOException("Не удалось получить эксклюзивный доступ к 1Cv8.1CD. Закройте сеансы 1С и проверьте доступ к файлу.", ex); }
        catch (IOException sharingError) when ((sharingError.HResult & 0xffff) is 32 or 33
            || !OperatingSystem.IsWindows() && (sharingError.HResult & 0xffff) == 11)
        {
            // Retain this exact handle through archiving. A separate probe followed by
            // reopening with shared access would let 1C open a previously closed base.
            try
            {
                var input = Open(FileShare.ReadWrite);
                shared = true;
                return input;
            }
            catch (IOException ex)
            { throw new IOException("Не удалось прочитать 1Cv8.1CD с совместным доступом. Другой процесс может запрещать чтение; закройте сеансы 1С и проверьте доступ к файлу.", ex); }
        }
        FileStream Open(FileShare share) => new(database, FileMode.Open, FileAccess.Read, share,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }
}
