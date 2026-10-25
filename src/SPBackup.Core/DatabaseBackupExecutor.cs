using System.Data;
using System.Data.Common;

namespace SPBackup.Core;

public enum SqlServerAuthentication { Windows, SqlServer }

public sealed record SqlServerBackupSettings
{
    public string Server { get; init; } = "";
    public string ServerBackupDirectory { get; init; } = "";
    public string AgentReadDirectory { get; init; } = "";
    public int TimeoutMinutes { get; init; } = 120;
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public SqlServerAuthentication Authentication { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? CredentialKey { get; init; }
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Server)) errors.Add("Укажите MS SQL.");
        errors.AddRange(SqlServerConnectionFactory.ValidateAuthentication(Authentication, CredentialKey));
        var path = ServerBackupDirectory ?? "";
        if (!(path.StartsWith('/') || path.StartsWith(@"\\") ||
            (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\')))
            errors.Add("Укажите полный каталог копий на стороне MS SQL.");
        if (string.IsNullOrWhiteSpace(AgentReadDirectory) || !Path.IsPathFullyQualified(AgentReadDirectory))
            errors.Add("Укажите полный локальный или UNC-путь к этому каталогу для чтения агентом.");
        if (TimeoutMinutes is < 1 or > 10080) errors.Add("Время копирования SQL должно быть от 1 до 10080 минут.");
        return errors;
    }
}

public sealed record PostgreSqlBackupSettings
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? CredentialKey { get; init; }
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 5432;
    public string Username { get; init; } = "postgres";
    public string PgDumpPath { get; init; } = "";
    public string PgRestorePath { get; init; } = "";
    public int TimeoutMinutes { get; init; } = 120;
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Username)) errors.Add("Укажите сервер и пользователя PostgreSQL.");
        if (CredentialKey is not null && (string.IsNullOrWhiteSpace(CredentialKey) || CredentialKey.Length > 512 || CredentialKey.Any(char.IsControl)))
            errors.Add("Сохраните пароль PostgreSQL заново.");
        if (CredentialKey is not null && (new[] { Host, Username }.Any(value => value is null || value == "*" || value.Any(char.IsControl)) || Host.Contains(',')))
            errors.Add("Для сохранённого пароля PostgreSQL укажите один конкретный сервер и пользователя без управляющих символов.");
        if (Port is < 1 or > 65535) errors.Add("Укажите порт PostgreSQL от 1 до 65535.");
        if (string.IsNullOrWhiteSpace(PgDumpPath) || !Path.IsPathFullyQualified(PgDumpPath) ||
            string.IsNullOrWhiteSpace(PgRestorePath) || !Path.IsPathFullyQualified(PgRestorePath))
            errors.Add("Укажите полные пути к pg_dump и pg_restore.");
        if (TimeoutMinutes is < 1 or > 10080) errors.Add("Время копирования PostgreSQL должно быть от 1 до 10080 минут.");
        return errors;
    }
}

public sealed class DatabaseBackupExecutor(IBackupProcessRunner processRunner, IArchivePasswordStore? passwords = null) : IBackupExecutor
{
    public Task<string> CreateAsync(BackupJob job, string workDirectory, CancellationToken cancellationToken) => job.Kind switch
    {
        DatabaseKind.OneCFile => new OneCBackupExecutor(processRunner, passwords).CreateAsync(job, workDirectory, cancellationToken),
        DatabaseKind.SqlServer => new SqlServerBackupExecutor().CreateAsync(job, workDirectory, cancellationToken),
        DatabaseKind.PostgreSql => new PostgreSqlBackupExecutor(processRunner).CreateAsync(job, workDirectory, cancellationToken),
        _ => throw new NotSupportedException("Неизвестный тип базы данных.")
    };
}

public sealed class SqlServerBackupExecutor(Func<BackupJob, DbConnection>? connectionFactory = null,
    ISqlServerCredentialStore? credentials = null) : IBackupExecutor
{
    internal static async Task<DbConnection> CreateConnectionAsync(BackupJob job,
        ISqlServerCredentialStore? credentials = null, CancellationToken token = default)
    {
        var settings = job.SqlServer ?? throw new InvalidDataException("Укажите настройки подключения MS SQL.");
        var errors = settings.Validate();
        if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
        return await SqlServerConnectionFactory.CreateAsync(settings.Server, settings.Authentication,
            settings.CredentialKey, "master", "SP Backup", credentials, token);
    }

    public async Task<string> CreateAsync(BackupJob job, string workDirectory, CancellationToken cancellationToken)
    {
        if (job.Kind != DatabaseKind.SqlServer) throw new ArgumentException("Нужно задание MS SQL.");
        var settings = job.SqlServer ?? throw new InvalidDataException("Укажите настройки подключения MS SQL.");
        var errors = job.Validate();
        if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
        Directory.CreateDirectory(workDirectory);
        var name = BackupFileName.Create(job, ".bak");
        var serverPath = settings.ServerBackupDirectory.TrimEnd('/', '\\') +
            (settings.ServerBackupDirectory.StartsWith('/') ? "/" : "\\") + name;
        var readablePath = Path.Combine(settings.AgentReadDirectory, name);
        await File.WriteAllTextAsync(Path.Combine(workDirectory, "sql-source.json"),
            System.Text.Json.JsonSerializer.Serialize(new { settings.Server, Database = job.Source,
                ServerPath = serverPath, AgentPath = readablePath }), cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(settings.TimeoutMinutes));
        try
        {
            await using var connection = connectionFactory?.Invoke(job) ?? await CreateConnectionAsync(job, credentials, timeout.Token);
            BackupProgressContext.Report(BackupStage.ConnectingSource);
            await connection.OpenAsync(timeout.Token);
            BackupProgressContext.Report(BackupStage.CreatingBackup);
            await using (var backup = connection.CreateCommand())
            {
                backup.CommandText = "BACKUP DATABASE @database TO DISK = @path WITH COPY_ONLY, CHECKSUM;";
                backup.CommandTimeout = settings.TimeoutMinutes * 60;
                AddParameter(backup, "@database", job.Source);
                AddParameter(backup, "@path", serverPath);
                await backup.ExecuteNonQueryAsync(timeout.Token);
            }
            BackupProgressContext.Report(BackupStage.Verifying);
            await using (var verify = connection.CreateCommand())
            {
                verify.CommandText = "RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM;";
                verify.CommandTimeout = settings.TimeoutMinutes * 60;
                AddParameter(verify, "@path", serverPath);
                await verify.ExecuteNonQueryAsync(timeout.Token);
            }
            if (!File.Exists(readablePath)) throw new FileNotFoundException(
                "SQL создал копию, но агент не может прочитать её. Проверьте соответствие каталогов и права.", readablePath);
            await using var source = new FileStream(readablePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (source.Length == 0) throw new InvalidDataException("SQL-копия пуста.");
            timeout.Token.ThrowIfCancellationRequested();
            // Delivery can register the SQL file in place when this is also the final directory.
            // Otherwise it streams/archives this verified source; it never removes the original.
            return Path.GetFullPath(readablePath);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Превышено время копирования MS SQL."); }
        catch (Exception ex) when (SqlServerErrorHints.Describe(ex) is { } hint)
        { throw new IOException(hint, ex); }
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name; parameter.DbType = DbType.String; parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

public sealed class PostgreSqlBackupExecutor(IBackupProcessRunner processRunner, IPostgreSqlCredentialStore? credentials = null) : IBackupExecutor
{
    public async Task<string> CreateAsync(BackupJob job, string workDirectory, CancellationToken cancellationToken)
    {
        if (job.Kind != DatabaseKind.PostgreSql) throw new ArgumentException("Нужно задание PostgreSQL.");
        var settings = job.PostgreSql ?? throw new InvalidDataException("Укажите настройки PostgreSQL.");
        var errors = job.Validate();
        if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
        if (!File.Exists(settings.PgDumpPath) || !File.Exists(settings.PgRestorePath))
            throw new FileNotFoundException("Не найдены pg_dump или pg_restore.");
        var work = Path.GetFullPath(workDirectory);
        Directory.CreateDirectory(work);
        var dump = Path.Combine(work, BackupFileName.Create(job, ".dump"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(settings.TimeoutMinutes));
        try
        {
            using var connection = await PostgreSqlConnectionEnvironment.OpenAsync(job, credentials, timeout.Token);
            var code = await processRunner.RunAsync(new BackupProcess(settings.PgDumpPath,
                ["--format=custom", "--no-password", "--host=" + settings.Host, "--port=" + settings.Port,
                 "--username=" + settings.Username, "--dbname=" + job.Source, "--file=" + dump], work)
                { ErrorLogPath = Path.Combine(work, "pg_dump.log"), DiscardStandardOutput = true, Environment = connection.Environment }, timeout.Token);
            if (code != 0) throw new IOException($"pg_dump завершился с кодом {code}. Проверьте доступ, пароль пользователя и совместимость утилиты. Журнал: {Path.Combine(work, "pg_dump.log")}");
            if (!File.Exists(dump) || new FileInfo(dump).Length == 0) throw new InvalidDataException("pg_dump не создал непустую копию.");
            code = await processRunner.RunAsync(new BackupProcess(settings.PgRestorePath, ["--list", dump], work)
                { ErrorLogPath = Path.Combine(work, "pg_restore.log"), DiscardStandardOutput = true, Environment = connection.Environment }, timeout.Token);
            if (code != 0) throw new InvalidDataException($"Проверка каталога дампа pg_restore завершилась с кодом {code}. Журнал: {Path.Combine(work, "pg_restore.log")}");
            timeout.Token.ThrowIfCancellationRequested();
            return dump;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Превышено время копирования PostgreSQL."); }
    }
}
