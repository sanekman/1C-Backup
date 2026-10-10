using System.Text.Json.Serialization;

namespace OneCBackup.Core;

public enum DatabaseKind { OneCFile, SqlServer, PostgreSql }
public enum OneCBackupMethod { PlatformDump, FileArchive }

// Credentials belong in the Windows credential store, never in this document.
public sealed record BackupJob
{
    [JsonRequired]
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public DatabaseKind Kind { get; init; }
    public string Source { get; init; } = "";
    public string DestinationDirectory { get; init; } = "";
    public StorageKind StorageKind { get; init; } = StorageKind.LocalDirectory;
    public FtpSettings? Ftp { get; init; }
    public TimeOnly DailyAt { get; init; } = new(23, 0);
    public ScheduleKind ScheduleKind { get; init; } = ScheduleKind.Daily;
    public WeekDays DaysOfWeek { get; init; } = WeekDays.Monday;
    public string TimeZoneId { get; init; } = "UTC";
    public int KeepLast { get; init; } = 7;
    public bool Enabled { get; init; } = true;
    public bool ArchiveCopies { get; init; }
    public OneCSettings? OneC { get; init; }
    public OneCBackupMethod OneCMethod { get; init; } = OneCBackupMethod.PlatformDump;
    public SqlServerBackupSettings? SqlServer { get; init; }
    public PostgreSqlBackupSettings? PostgreSql { get; init; }

    [JsonIgnore]
    public bool ShouldArchiveCopy => ArchiveCopies &&
        !(Kind == DatabaseKind.OneCFile && OneCMethod == OneCBackupMethod.FileArchive);

    [JsonIgnore]
    public string ScheduleDescription => ScheduleKind switch
    {
        ScheduleKind.Manual => "Вручную",
        ScheduleKind.Daily => $"Ежедневно в {DailyAt:HH:mm}",
        ScheduleKind.Weekly => $"По дням недели в {DailyAt:HH:mm}",
        _ => "Некорректное расписание"
    };

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Id == Guid.Empty) errors.Add("Не указан идентификатор задания.");
        if (string.IsNullOrWhiteSpace(Name)) errors.Add("Укажите название задания.");
        if (!Enum.IsDefined(Kind)) errors.Add("Неизвестный тип базы данных.");
        if (string.IsNullOrWhiteSpace(Source)) errors.Add("Укажите источник базы данных.");
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
            if (Source?.Contains('=') == true) errors.Add("Укажите имя базы PostgreSQL, а не строку подключения.");
            if (PostgreSql is not null) errors.AddRange(PostgreSql.Validate());
        }
        return errors;
    }

    public IReadOnlyList<string> ValidateSchedule()
    {
        var errors = new List<string>();
        if (!Enum.IsDefined(ScheduleKind)) errors.Add("Неизвестный тип расписания.");
        if (ScheduleKind == ScheduleKind.Weekly && (DaysOfWeek == WeekDays.None || ((int)DaysOfWeek & ~127) != 0))
            errors.Add("Выберите хотя бы один корректный день недели.");
        try { TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId ?? ""); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        { errors.Add("Укажите существующий часовой пояс."); }
        return errors;
    }
}
