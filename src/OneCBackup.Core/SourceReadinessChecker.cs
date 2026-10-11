using System.Data;
using System.Data.Common;

namespace OneCBackup.Core;

public sealed record SourceCheckReport(bool Succeeded, string Details, bool Cancelled = false);

/// <summary>Reads source availability without creating a database backup or running the 1C platform.</summary>
public sealed class SourceReadinessChecker(IBackupProcessRunner processRunner, string defaultWorkRoot,
    Func<BackupJob, DbConnection>? sqlConnectionFactory = null)
{
    public async Task<SourceCheckReport> CheckAsync(BackupJob job, CancellationToken cancellationToken = default)
    {
        var errors = job.Validate();
        if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            return job.Kind switch
            {
                DatabaseKind.OneCFile => CheckFile(job, timeout.Token),
                DatabaseKind.SqlServer => await CheckSqlAsync(job, timeout.Token),
                DatabaseKind.PostgreSql => await CheckPostgreSqlAsync(job, timeout.Token),
                _ => throw new NotSupportedException("Неизвестный тип базы.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return new(false, "Проверка источника отменена.", true); }
        catch (OperationCanceledException)
        { return new(false, "Время проверки источника превысило 30 секунд. Проверьте подключение и доступ."); }
        catch (Exception ex)
        { return new(false, ex.Message); }
    }

    private static SourceCheckReport CheckFile(BackupJob job, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (job.OneCMethod == OneCBackupMethod.PlatformDump && !File.Exists(job.OneC!.ExecutablePath))
            throw new FileNotFoundException("Не найдена программа 1С (1cv8.exe). Выберите установленную платформу.");
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
                job.OneCMethod == OneCBackupMethod.FileArchive ? FileShare.None : FileShare.ReadWrite);
        }
        catch (IOException) when (job.OneCMethod == OneCBackupMethod.FileArchive)
        { throw new IOException("Не удалось получить эксклюзивный доступ к 1Cv8.1CD. Закройте сеансы 1С и проверьте доступ."); }
        using (input)
        {
            if (input.Length == 0) throw new InvalidDataException("Файл базы 1Cv8.1CD пуст.");
            token.ThrowIfCancellationRequested();
        }
        return new(true, job.OneCMethod == OneCBackupMethod.FileArchive
            ? "Файл базы найден; эксклюзивный доступ получен и освобождён. Сеансы должны быть закрыты также при копировании. Содержимое базы не проверялось."
            : "Файл базы и программа 1С найдены; доступ на чтение проверен. Платформа не запускалась. Выгрузка .dt и эксклюзивное подключение проверяются первой копией.");
    }

    private async Task<SourceCheckReport> CheckSqlAsync(BackupJob job, CancellationToken token)
    {
        await using var connection = sqlConnectionFactory?.Invoke(job) ?? SqlServerBackupExecutor.CreateConnection(job);
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
            return new(false, "База недоступна или нет права BACKUP DATABASE. Проверьте имя базы и права текущего пользователя Windows.");
        return new(true, "Подключение к SQL Server, доступ к базе и право BACKUP DATABASE проверены. Копия не создавалась. Права службы на серверную папку, сопоставление путей, VERIFYONLY и восстановление проверяются отдельно.");
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
            var code = await processRunner.RunAsync(new BackupProcess(psql,
                ["--no-password", "--host=" + settings.Host, "--port=" + settings.Port, "--username=" + settings.Username,
                    "--dbname=" + job.Source, "--set=ON_ERROR_STOP=1", "--command=SELECT 1;"], work)
                { DiscardStandardOutput = true, ErrorLogPath = Path.Combine(work, "psql.log") }, token);
            token.ThrowIfCancellationRequested();
            return code == 0
                ? new(true, "Подключение к базе PostgreSQL проверено через psql и SELECT 1. Дамп не создавался. Доступ к данным всех таблиц, совместимость pg_dump и восстановление проверяются первой копией и тестовым восстановлением.")
                : new(false, $"psql завершился с кодом {code}. Проверьте имя базы, сервер, пользователя, файл паролей и настройки TLS.");
        }
        finally
        {
            if (ownsWork) Directory.Delete(work, recursive: true);
        }
    }
}
