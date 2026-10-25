using System.Data;
using System.Data.Common;

namespace SPBackup.Core;

public sealed record SourceCheckReport(bool Succeeded, string Details, bool Cancelled = false);

/// <summary>Reads source availability without creating a database backup or running the 1C platform.</summary>
public sealed class SourceReadinessChecker(IBackupProcessRunner processRunner, string defaultWorkRoot,
    Func<BackupJob, DbConnection>? sqlConnectionFactory = null, Func<BackupJob, WebDavClient>? webDavClientFactory = null,
    ISqlServerCredentialStore? sqlCredentials = null, IPostgreSqlCredentialStore? postgreSqlCredentials = null)
{
    public async Task<SourceCheckReport> CheckAsync(BackupJob job, CancellationToken cancellationToken = default)
    {
        var errors = job.Validate();
        if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        DiagnosticLog.Current.Write(DiagnosticEvent.SourceCheckStarted, jobId: job.Id, sourceKind: job.Kind);
        try
        {
            var result = job.Kind switch
            {
                DatabaseKind.OneCFile => CheckFile(job, timeout.Token),
                DatabaseKind.SqlServer => await CheckSqlAsync(job, timeout.Token),
                DatabaseKind.PostgreSql => await CheckPostgreSqlAsync(job, timeout.Token),
                DatabaseKind.FilesAndFolders => CheckFiles(job, timeout.Token),
                DatabaseKind.WebDav => await CheckWebDavAsync(job, timeout.Token),
                _ => throw new NotSupportedException("Неизвестный тип базы.")
            };
            DiagnosticLog.Current.Write(DiagnosticEvent.SourceCheckCompleted, result.Succeeded ? DiagnosticLevel.Info : DiagnosticLevel.Warning,
                jobId: job.Id, sourceKind: job.Kind, values: new(Code: result.Succeeded ? 0 : 1));
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { DiagnosticLog.Current.Write(DiagnosticEvent.SourceCheckCompleted, DiagnosticLevel.Warning, jobId: job.Id, sourceKind: job.Kind, values: new(Code: 2)); return new(false, "Проверка источника отменена.", true); }
        catch (OperationCanceledException)
        { DiagnosticLog.Current.Write(DiagnosticEvent.SourceCheckFailed, DiagnosticLevel.Warning, jobId: job.Id, sourceKind: job.Kind, values: new(Code: 3)); return new(false, "Время проверки источника превысило 30 секунд. Проверьте подключение и доступ."); }
        catch (Exception ex)
        { DiagnosticLog.Current.Write(DiagnosticEvent.SourceCheckFailed, DiagnosticLevel.Error, ex, jobId: job.Id, sourceKind: job.Kind); return new(false, job.Kind == DatabaseKind.SqlServer ? SqlServerErrorHints.Describe(ex) ?? ex.Message : ex.Message); }
    }

    private async Task<SourceCheckReport> CheckWebDavAsync(BackupJob job, CancellationToken token)
    {
        using var client = webDavClientFactory?.Invoke(job) ?? await WebDavClient.OpenAsync(job, token);
        var entries = await client.ScanAsync(token);
        return new(true, $"WebDAV подтвердил список: {entries.Count(entry => !entry.IsDirectory)} файлов, {entries.Count(entry => entry.IsDirectory)} папок. Содержимое файлов не скачивалось. Доступ к скачиванию проверяется первой копией.");
    }

    private static SourceCheckReport CheckFiles(BackupJob job, CancellationToken token)
    {
        var source = Path.GetFullPath(job.Source); var isFile = File.Exists(source);
        if (!isFile && !Directory.Exists(source)) throw new FileNotFoundException("Не найден исходный файл или папка.");
        var scan = FileBackupPaths.Scan(source, isFile, token);
        foreach (var file in scan.Files)
        {
            token.ThrowIfCancellationRequested();
            using var input = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        return new(true, $"Доступ на чтение проверен: {scan.Files.Count} файлов, {scan.Directories.Count} папок. Копия не создавалась; источник должен оставаться без изменений во время копирования.");
    }

    private static SourceCheckReport CheckFile(BackupJob job, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (job.OneCMethod == OneCBackupMethod.PlatformDump) OneCPlatformLocator.Resolve(job.OneC!);
        var source = Path.GetFullPath(job.Source);
        var file = string.Equals(Path.GetFileName(source), "1Cv8.1CD", StringComparison.OrdinalIgnoreCase)
            ? source : Path.Combine(source, "1Cv8.1CD");
        if (!File.Exists(file)) throw new FileNotFoundException("Не найден файл базы 1Cv8.1CD. Проверьте выбранную папку.");
        if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Выберите настоящий файл базы, а не символическую ссылку.");
        FileStream input;
        try
        {
            input = new FileStream(file, FileMode.Open, FileAccess.Read,
                job.OneCMethod == OneCBackupMethod.FileArchive && !job.AllowOpenOneCDatabase ? FileShare.None : FileShare.ReadWrite);
        }
        catch (IOException) when (job.OneCMethod == OneCBackupMethod.FileArchive)
        { throw new IOException(job.AllowOpenOneCDatabase
            ? "Не удалось прочитать 1Cv8.1CD с совместным доступом. Закройте сеансы 1С и проверьте доступ."
            : "Не удалось получить эксклюзивный доступ к 1Cv8.1CD. Закройте сеансы 1С и проверьте доступ."); }
        using (input)
        {
            if (input.Length == 0) throw new InvalidDataException("Файл базы 1Cv8.1CD пуст.");
            token.ThrowIfCancellationRequested();
        }
        return new(true, job.OneCMethod == OneCBackupMethod.FileArchive
            ? job.AllowOpenOneCDatabase
                ? "Файл базы найден; доступ на чтение проверен. " + OneCFileArchiveExecutor.OpenDatabaseWarning
                : "Файл базы найден; эксклюзивный доступ получен и освобождён. Сеансы должны быть закрыты также при копировании. Содержимое базы не проверялось."
            : "Файл базы и программа 1С найдены; доступ на чтение проверен. Платформа не запускалась. Выгрузка .dt и эксклюзивное подключение проверяются первой копией.");
    }

    private async Task<SourceCheckReport> CheckSqlAsync(BackupJob job, CancellationToken token)
    {
        await using var connection = sqlConnectionFactory?.Invoke(job) ?? await SqlServerBackupExecutor.CreateConnectionAsync(job, sqlCredentials, token);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 20;
        command.CommandText = "SELECT CASE WHEN DB_ID(@database) IS NOT NULL AND HAS_DBACCESS(@database) = 1 "
            + "AND HAS_PERMS_BY_NAME(@database, N'DATABASE', N'BACKUP DATABASE') = 1 THEN 1 ELSE 0 END;";
        var parameter = command.CreateParameter(); parameter.ParameterName = "@database";
        parameter.DbType = DbType.String; parameter.Value = job.Source; command.Parameters.Add(parameter);
        var value = await command.ExecuteScalarAsync(token);
        token.ThrowIfCancellationRequested();
        if (value is not int result || result != 1)
            return new(false, "Подключение к MS SQL установлено, но база недоступна или нет права BACKUP DATABASE. Проверьте имя базы в задании и попросите администратора проверить доступ и права выбранного пользователя. При Windows-входе служба использует свою учётную запись.");
        return new(true, "Подключение к MS SQL, доступ к базе и право BACKUP DATABASE проверены. Копия не создавалась. Права службы на серверную папку, сопоставление путей, VERIFYONLY и восстановление проверяются отдельно.");
    }

    private async Task<SourceCheckReport> CheckPostgreSqlAsync(BackupJob job, CancellationToken token)
    {
        var settings = job.PostgreSql!;
        if (!File.Exists(settings.PgDumpPath) || !File.Exists(settings.PgRestorePath))
            throw new FileNotFoundException("Не найдены pg_dump или pg_restore. Выберите установленные утилиты PostgreSQL.");
        var psql = Path.Combine(Path.GetDirectoryName(settings.PgDumpPath)!, OperatingSystem.IsWindows() ? "psql.exe" : "psql");
        if (!File.Exists(psql))
            return new(false, "Для проверки подключения нужен psql рядом с pg_dump. Установите утилиты PostgreSQL; само копирование использует pg_dump и pg_restore.");
        var work = Path.Combine(Path.GetFullPath(job.TemporaryDirectory ?? defaultWorkRoot), "source-check-" + Guid.NewGuid().ToString("N"));
        var ownsWork = false;
        try
        {
            if (Directory.Exists(work) || File.Exists(work)) throw new IOException("Тестовая папка уже существует.");
            Directory.CreateDirectory(work); ownsWork = true;
            using var connection = await PostgreSqlConnectionEnvironment.OpenAsync(job, postgreSqlCredentials, token);
            var code = await processRunner.RunAsync(new BackupProcess(psql,
                ["--no-password", "--host=" + settings.Host, "--port=" + settings.Port, "--username=" + settings.Username,
                    "--dbname=" + job.Source, "--set=ON_ERROR_STOP=1", "--command=SELECT 1;"], work)
                { DiscardStandardOutput = true, ErrorLogPath = Path.Combine(work, "psql.log"), Environment = connection.Environment }, token);
            token.ThrowIfCancellationRequested();
            return code == 0
                ? new(true, "Подключение к базе PostgreSQL проверено через psql и SELECT 1. Дамп не создавался. Доступ к данным всех таблиц, совместимость pg_dump и восстановление проверяются первой копией и тестовым восстановлением.")
                : new(false, $"Не удалось проверить подключение к PostgreSQL (psql, код {code}). Проверьте сервер, порт, имя базы и пользователя. В редакторе задания проверьте выбранный способ входа и при необходимости задайте пароль заново. Если сервер требует TLS, попросите администратора проверить настройки и сертификаты. Код выхода не определяет точную причину; дамп при этой проверке не создавался.");
        }
        finally
        {
            if (ownsWork) Directory.Delete(work, recursive: true);
        }
    }
}
