using System.Text.Json.Serialization;

namespace SPBackup.Core;

public enum DatabaseKind { OneCFile, SqlServer, PostgreSql, FilesAndFolders, WebDav }
public enum OneCBackupMethod { PlatformDump, FileArchive }
public enum FileBackupMode { Full, Incremental }

// Credentials belong in the Windows credential store, never in this document.
public sealed record BackupJob
{
    // Execution provenance travels with the selected immutable job, never with its configuration JSON.
    [JsonIgnore]
    internal BackupConfigurationStamp? ExecutionConfiguration { get; init; }
    [JsonRequired]
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public DatabaseKind Kind { get; init; }
    public string Source { get; init; } = "";
    public string DestinationDirectory { get; init; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TemporaryDirectory { get; init; }
    public StorageKind StorageKind { get; init; } = StorageKind.LocalDirectory;
    public FtpSettings? Ftp { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GoogleDriveSettings? GoogleDrive { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RemoteGatewaySettings? RemoteGateway { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<BackupDestination>? AdditionalDestinations { get; init; }

    [JsonIgnore]
    public IReadOnlyList<BackupJob> StorageJobs => new[] { this with { AdditionalDestinations = null } }
        .Concat((AdditionalDestinations ?? []).Select(destination => destination.ApplyTo(this))).ToArray();
    public TimeOnly DailyAt { get; init; } = new(23, 0);
    public IReadOnlyList<TimeOnly>? AdditionalDailyTimes { get; init; }
    public ScheduleKind ScheduleKind { get; init; } = ScheduleKind.Daily;
    public WeekDays DaysOfWeek { get; init; } = WeekDays.Monday;
    public string TimeZoneId { get; init; } = "UTC";
    public int KeepLast { get; init; } = 7;
    public bool Enabled { get; init; } = true;
    public bool ArchiveCopies { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArchivePasswordKey { get; init; }
    public OneCSettings? OneC { get; init; }
    public bool AllowOpenOneCDatabase { get; init; }
    public OneCBackupMethod OneCMethod { get; init; } = OneCBackupMethod.PlatformDump;
    public SqlServerBackupSettings? SqlServer { get; init; }
    public PostgreSqlBackupSettings? PostgreSql { get; init; }
    public FileBackupMode FileBackupMode { get; init; } = FileBackupMode.Full;
    public int FullBackupEvery { get; init; } = 7;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DayOfWeek? FullBackupDayOfWeek { get; init; }
    public WebDavSettings? WebDav { get; init; }

    [JsonIgnore]
    public bool IsFileSnapshot => Kind is DatabaseKind.FilesAndFolders or DatabaseKind.WebDav;

    [JsonIgnore]
    public bool ShouldArchiveCopy => ArchiveCopies && !IsFileSnapshot &&
        !(Kind == DatabaseKind.OneCFile && OneCMethod == OneCBackupMethod.FileArchive);

    [JsonIgnore]
    public IReadOnlyList<TimeOnly> ScheduledTimes => new[] { DailyAt }.Concat(AdditionalDailyTimes ?? []).Order().ToArray();

    [JsonIgnore]
    public string ScheduleDescription => ScheduleKind switch
    {
        ScheduleKind.Manual => "Вручную",
        ScheduleKind.SystemStartup => "При запуске системы",
        ScheduleKind.Daily => "Ежедневно в " + string.Join(", ", ScheduledTimes.Select(time => time.ToString("HH:mm"))),
        ScheduleKind.Weekly => "По дням недели в " + string.Join(", ", ScheduledTimes.Select(time => time.ToString("HH:mm"))),
        _ => "Некорректное расписание"
    };

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Id == Guid.Empty) errors.Add("Не указан идентификатор задания.");
        if (string.IsNullOrWhiteSpace(Name)) errors.Add("Укажите название задания.");
        if (!Enum.IsDefined(Kind)) errors.Add("Неизвестный тип базы данных.");
        if (!Enum.IsDefined(FileBackupMode)) errors.Add("Неизвестный способ копирования файлов.");
        if (FullBackupDayOfWeek is { } fullDay && !Enum.IsDefined(fullDay)) errors.Add("Выберите корректный день недели для полной копии.");
        if (FullBackupEvery is < 1 or > 365) errors.Add("Полная копия должна создаваться не реже каждой 365-й копии и не чаще каждой первой.");
        if (Kind == DatabaseKind.FilesAndFolders && !Path.IsPathFullyQualified(Source ?? ""))
            errors.Add("Укажите полный путь к файлу или папке.");
        if (Kind == DatabaseKind.WebDav)
        {
            errors.AddRange(WebDavSettings.ValidateSource(Source));
            if (WebDav is null) errors.Add("Сохраните учётные данные WebDAV.");
            else errors.AddRange(WebDav.Validate());
        }
        if (string.IsNullOrWhiteSpace(Source)) errors.Add("Укажите источник базы данных.");
        if (TemporaryDirectory is not null && (string.IsNullOrWhiteSpace(TemporaryDirectory)
            || !Path.IsPathFullyQualified(TemporaryDirectory) || TemporaryDirectory.Any(char.IsControl)))
            errors.Add("Для временной папки укажите полный путь, например D:\\SP-Backup-Temp.");
        if (string.IsNullOrWhiteSpace(DestinationDirectory)) errors.Add("Укажите каталог резервных копий.");
        var destination = DestinationDirectory ?? "";
        if (!Enum.IsDefined(StorageKind)) errors.Add("Неизвестный тип хранилища.");
        if (StorageKind == StorageKind.NetworkShare &&
            (!destination.StartsWith(@"\\") || destination.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length < 2))
            errors.Add(@"Для сетевого диска укажите UNC-путь, например \\server\share\backups.");
        if (StorageKind == StorageKind.Ftp)
        {
            if (Ftp is null) errors.Add("Укажите настройки FTP-сервера.");
            else errors.AddRange(Ftp.Validate());
            if (!destination.StartsWith('/') || destination.Contains('\\') ||
                destination.Any(char.IsControl) || destination.Split('/').Contains(".."))
                errors.Add("Для FTP укажите абсолютный каталог, например /backups, без переходов '..'.");
        }
        if (StorageKind == StorageKind.GoogleDrive)
        {
            if (GoogleDrive is null) errors.Add("Подключите Google Drive.");
            else errors.AddRange(GoogleDrive.Validate());
            if (!GoogleDriveSettings.IsId(destination)) errors.Add("Подключите папку Google Drive.");
        }
        if (StorageKind == StorageKind.RemoteGateway)
        {
            if (RemoteGateway is null) errors.Add("Подключите шлюз хранилища."); else errors.AddRange(RemoteGateway.Validate());
            if (string.IsNullOrWhiteSpace(ArchivePasswordKey)) errors.Add("Для удалённого шлюза задайте пароль архива.");
        }
        if (AdditionalDestinations is not null)
        {
            if (AdditionalDestinations.Count > 7) errors.Add("Можно указать не более 8 хранилищ для одного задания.");
            for (var index = 0; index < Math.Min(AdditionalDestinations.Count, 7); index++)
            {
                var destinationSettings = AdditionalDestinations[index];
                if (destinationSettings is null) { errors.Add("Не заданы параметры дополнительного хранилища."); continue; }
                var target = destinationSettings.ApplyTo(this);
                errors.AddRange(target.Validate().Select(error => $"Хранилище {index + 2}: {error}"));
                if (target.StorageKind is StorageKind.LocalDirectory or StorageKind.NetworkShare && !Path.IsPathFullyQualified(target.DestinationDirectory))
                    errors.Add($"Хранилище {index + 2}: укажите полный путь к папке.");
            }
            if (errors.Count == 0)
            {
                try
                {
                    var identities = StorageJobs.Select(target => target.StorageKind == StorageKind.Ftp
                        ? StorageAddress.Identity(target with { Ftp = target.Ftp! with { UseTls = false } }) : target.StorageKind is StorageKind.GoogleDrive or StorageKind.RemoteGateway ? StorageAddress.Identity(target) : "file:" + (OperatingSystem.IsWindows()
                            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.DestinationDirectory)).ToUpperInvariant()
                            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.DestinationDirectory))));
                    if (identities.Distinct(StringComparer.Ordinal).Count() != AdditionalDestinations.Count + 1)
                        errors.Add("Одно и то же хранилище указано несколько раз.");
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                { errors.Add("Укажите корректные пути хранилищ."); }
            }
        }
        if (ArchivePasswordKey is not null)
        {
            if (string.IsNullOrWhiteSpace(ArchivePasswordKey) || ArchivePasswordKey.Length > 512 || ArchivePasswordKey.Any(char.IsControl))
                errors.Add("Укажите корректный идентификатор пароля архива.");
            if (!ArchiveCopies && !IsFileSnapshot && !(Kind == DatabaseKind.OneCFile && OneCMethod == OneCBackupMethod.FileArchive))
                errors.Add("Для шифрования копии включите архивацию в ZIP.");
        }
        if (KeepLast < 1) errors.Add("Нужно сохранять хотя бы одну успешную копию.");
        if (Kind == DatabaseKind.OneCFile)
        {
            if (!Enum.IsDefined(OneCMethod)) errors.Add("Неизвестный способ резервного копирования 1С.");
            if (OneCMethod == OneCBackupMethod.PlatformDump && OneC is not null) errors.AddRange(OneC.Validate());
        }
        errors.AddRange(ValidateSchedule());
        if (Kind == DatabaseKind.SqlServer && SqlServer is not null) errors.AddRange(SqlServer.Validate());
        if (Kind == DatabaseKind.PostgreSql)
        {
            var database = Source?.TrimStart() ?? "";
            if (database.Contains('=') || database.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)
                || database.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
                errors.Add("Укажите имя базы PostgreSQL, а не строку подключения.");
            if (PostgreSql is not null) errors.AddRange(PostgreSql.Validate());
            if (PostgreSql?.CredentialKey is not null && (Source is null || Source == "*" || Source.Any(char.IsControl)))
                errors.Add("Для сохранённого пароля PostgreSQL укажите конкретное имя базы без управляющих символов.");
        }
        return errors;
    }

    public IReadOnlyList<string> ValidateSchedule()
    {
        var errors = new List<string>();
        if (!Enum.IsDefined(ScheduleKind)) errors.Add("Неизвестный тип расписания.");
        if (ScheduleKind != ScheduleKind.SystemStartup && AdditionalDailyTimes?.Count > 47) errors.Add("Укажите не более 48 запусков в день.");
        if (ScheduleKind != ScheduleKind.SystemStartup && AdditionalDailyTimes is not null && (AdditionalDailyTimes.Contains(DailyAt)
            || AdditionalDailyTimes.Distinct().Count() != AdditionalDailyTimes.Count))
            errors.Add("Время каждого запуска должно быть уникальным.");
        if (ScheduleKind == ScheduleKind.Weekly && (DaysOfWeek == WeekDays.None || ((int)DaysOfWeek & ~127) != 0))
            errors.Add("Выберите хотя бы один корректный день недели.");
        try { BackupSchedule.ResolveTimeZone(TimeZoneId ?? ""); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        { errors.Add("Укажите существующий часовой пояс."); }
        return errors;
    }
}
