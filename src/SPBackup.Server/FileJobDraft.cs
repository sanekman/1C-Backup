using System.Globalization;

namespace SPBackup.Server;

/// <summary>Browser form values remain strings until validated, so a failed step preserves the draft.</summary>
public sealed partial class FileJobDraft
{
    public int Step { get; set; } = 1;
    public string Revision { get; set; } = "";
    public string JobId { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string SuggestedName { get; set; } = "";
    public string Kind { get; set; } = "FilesAndFolders";
    public string Source { get; set; } = "";
    public bool AllowOpen { get; set; }
    public string Schedule { get; set; } = "Manual";
    public bool Enabled { get; set; } = true;
    public string Times { get; set; } = "23:00";
    public string TimeZone { get; set; } = "Local";
    public bool Monday { get; set; } = true;
    public bool Tuesday { get; set; }
    public bool Wednesday { get; set; }
    public bool Thursday { get; set; }
    public bool Friday { get; set; }
    public bool Saturday { get; set; }
    public bool Sunday { get; set; }
    public string Destination { get; set; } = "";
    public string Temporary { get; set; } = "";
    public string Keep { get; set; } = "7";
    public string BackupMode { get; set; } = "Full";
    public string FullEvery { get; set; } = "7";
    public string FullDay { get; set; } = "";

    public static readonly (string Key, string Label)[] Days = [("Monday", "Понедельник"), ("Tuesday", "Вторник"),
        ("Wednesday", "Среда"), ("Thursday", "Четверг"), ("Friday", "Пятница"), ("Saturday", "Суббота"), ("Sunday", "Воскресенье")];
    public IReadOnlyList<string> SelectedDays => Days.Where(d => DayValue(d.Key)).Select(d => d.Key).ToArray();
    public bool DayValue(string day) => day switch { "Monday" => Monday, "Tuesday" => Tuesday, "Wednesday" => Wednesday,
        "Thursday" => Thursday, "Friday" => Friday, "Saturday" => Saturday, "Sunday" => Sunday, _ => false };
    public static string ScheduleLabel(string value) => value switch { "Manual" => "Только вручную", "Daily" => "Каждый день",
        "Weekly" => "В выбранные дни недели", "SystemStartup" => "При запуске Windows", _ => "Неизвестное расписание" };

    public IReadOnlyList<string> ValidateStep(int step)
    {
        var errors = new List<string>();
        if (step == 1)
        {
            if (string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > 256 || Name.Any(char.IsControl)) errors.Add("Введите название задания: от 1 до 256 символов.");
            if (Kind is not ("FilesAndFolders" or "OneCFile")) errors.Add("Выберите файлы и папки или файловую базу 1С.");
            if (!FileConfigurationContract.WindowsPath(Source?.Trim())) errors.Add("Укажите полный путь на компьютере агента: C:\\Данные или \\\\сервер\\папка. Относительные пути и адреса сайтов не подходят.");
        }
        if (step == 2)
        {
            if (Schedule is not ("Manual" or "Daily" or "Weekly" or "SystemStartup")) errors.Add("Выберите расписание.");
            if (Schedule is "Daily" or "Weekly" && !TryTimes(out _)) errors.Add("Укажите от 1 до 48 разных времён в формате чч:мм через запятую. Например: 09:00, 23:00.");
            if (Schedule == "Weekly" && SelectedDays.Count == 0) errors.Add("Выберите хотя бы один день недели.");
            try { if (TimeZone != "Local") _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZone ?? ""); }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException) { errors.Add("Выберите часовой пояс расписания."); }
        }
        if (step == 3)
        {
            if (!FileConfigurationContract.WindowsPath(Destination?.Trim())) errors.Add("Укажите полную папку для копий на диске агента или сетевой путь UNC.");
            if (!string.IsNullOrWhiteSpace(Temporary) && !FileConfigurationContract.WindowsPath(Temporary.Trim())) errors.Add("Рабочая папка должна быть полным Windows-путём. Можно оставить её пустой.");
            if (!int.TryParse(Keep, out var keep) || keep is < 1 or > 10000) errors.Add("Количество хранимых архивов должно быть от 1 до 10000.");
            if (BackupMode is not ("Full" or "Incremental")) errors.Add("Выберите полные или инкрементные копии.");
            if (!int.TryParse(FullEvery, out var every) || every is < 1 or > 365) errors.Add("Интервал полной копии должен быть от 1 до 365 архивов.");
            if (FullDay != "" && (!int.TryParse(FullDay, out var day) || day is < 0 or > 6)) errors.Add("Выберите день полной копии или вариант «По числу архивов».");
            var source = (Source?.Trim() ?? "").Replace('/', '\\');
            if (Kind == "OneCFile" && source.EndsWith("\\1Cv8.1CD", StringComparison.OrdinalIgnoreCase)) source = source[..source.LastIndexOf('\\')];
            if (FileConfigurationContract.WindowsPath(source) && FileConfigurationContract.WindowsPath(Destination?.Trim())
                && FileConfigurationContract.Overlap(source, Destination!.Trim())) errors.Add("Источник и папка копий должны находиться отдельно: одна не должна содержать другую.");
            if (!string.IsNullOrWhiteSpace(Temporary) && (FileConfigurationContract.Overlap(source, Temporary.Trim())
                || FileConfigurationContract.Overlap(Destination?.Trim() ?? "", Temporary.Trim()))) errors.Add("Рабочая папка не должна пересекаться с источником и папкой копий.");
            errors.AddRange(ValidateAdditionalDestinations(source));
        }
        return errors;
    }

    private bool TryTimes(out TimeOnly[] values)
    {
        var parts = (Times ?? "").Split(',', StringSplitOptions.TrimEntries); var result = new List<TimeOnly>();
        foreach (var part in parts)
            if (TimeOnly.TryParseExact(part, ["H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss", "H:mm:ss.FFFFFFF", "HH:mm:ss.FFFFFFF"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) result.Add(time);
            else { values = []; return false; }
        values = result.ToArray(); return values.Length is >= 1 and <= 48 && values.Distinct().Count() == values.Length;
    }
    public ManagedFileJob Build()
    {
        var errors = Enumerable.Range(1, 3).SelectMany(ValidateStep).ToArray();
        if (errors.Length != 0) throw new InvalidDataException(string.Join(" ", errors));
        _ = TryTimes(out var times);
        var path = Destination.Trim();
        var job = new ManagedFileJob { Id = Guid.Parse(JobId), Name = Name.Trim(), Kind = Kind, Source = Source.Trim(),
            AllowOpenOneCDatabase = Kind == "OneCFile" && AllowOpen, ScheduleKind = Schedule, Enabled = Enabled,
            DailyAt = times.Length == 0 ? new(23, 0) : times[0], AdditionalDailyTimes = times.Length > 1 ? times[1..] : null,
            DaysOfWeek = SelectedDays.Count == 0 ? "None" : string.Join(", ", SelectedDays), TimeZoneId = TimeZone,
            DestinationDirectory = path, StorageKind = path.StartsWith(@"\\") ? "NetworkShare" : "LocalDirectory",
            TemporaryDirectory = string.IsNullOrWhiteSpace(Temporary) ? null : Temporary.Trim(), KeepLast = int.Parse(Keep),
            FileBackupMode = Kind == "OneCFile" ? "Full" : BackupMode, FullBackupEvery = int.Parse(FullEvery),
            FullBackupDayOfWeek = Kind == "OneCFile" || FullDay == "" ? null : (DayOfWeek)int.Parse(FullDay),
            AdditionalDestinations = BuildAdditionalDestinations() };
        FileConfigurationContract.Validate(1, [job]); return job;
    }
    public static FileJobDraft From(ManagedFileJob job, long revision) => new() {
        Revision = revision.ToString(CultureInfo.InvariantCulture), JobId = job.Id.ToString(), Name = job.Name, Kind = job.Kind,
        Source = job.Source, AllowOpen = job.AllowOpenOneCDatabase, Schedule = job.ScheduleKind, Enabled = job.Enabled,
        Times = string.Join(", ", new[] { job.DailyAt }.Concat(job.AdditionalDailyTimes ?? []).Select(t => t.ToString(t.Second == 0 && t.Ticks % TimeSpan.TicksPerSecond == 0 ? "HH:mm" : "HH:mm:ss.fffffff", CultureInfo.InvariantCulture))),
        TimeZone = job.TimeZoneId, Monday = job.DaysOfWeek.Split(',').Any(d => d.Trim() == "Monday"),
        Tuesday = job.DaysOfWeek.Contains("Tuesday"), Wednesday = job.DaysOfWeek.Contains("Wednesday"), Thursday = job.DaysOfWeek.Contains("Thursday"),
        Friday = job.DaysOfWeek.Contains("Friday"), Saturday = job.DaysOfWeek.Contains("Saturday"), Sunday = job.DaysOfWeek.Contains("Sunday"),
        Destination = job.DestinationDirectory, Temporary = job.TemporaryDirectory ?? "", Keep = job.KeepLast.ToString(CultureInfo.InvariantCulture),
        BackupMode = job.FileBackupMode, FullEvery = job.FullBackupEvery.ToString(CultureInfo.InvariantCulture), FullDay = job.FullBackupDayOfWeek is { } day ? ((int)day).ToString() : "",
        AdditionalDestinations = (job.AdditionalDestinations ?? []).Select(d => new FileDestinationDraft {
            Destination = d.DestinationDirectory, Keep = d.KeepLast?.ToString(CultureInfo.InvariantCulture) ?? "" }).ToList() };

    public IEnumerable<KeyValuePair<string, string>> HiddenFields()
    {
        string[][] visible = [[], [nameof(Name), nameof(Kind), nameof(Source), nameof(AllowOpen)],
            [nameof(Schedule), nameof(Enabled), nameof(Times), nameof(TimeZone), ..Days.Select(d => d.Key)],
            [nameof(Destination), nameof(Temporary), nameof(Keep), nameof(BackupMode), nameof(FullEvery), nameof(FullDay)]];
        // Only simple explicit form properties are carried forward; computed properties are never posted.
        string[] all = [nameof(Step), nameof(Revision), nameof(JobId), nameof(Name), nameof(SuggestedName), nameof(Kind), nameof(Source), nameof(AllowOpen),
            nameof(Schedule), nameof(Enabled), nameof(Times), nameof(TimeZone), ..Days.Select(d => d.Key), nameof(Destination),
            nameof(Temporary), nameof(Keep), nameof(BackupMode), nameof(FullEvery), nameof(FullDay)];
        foreach (var key in all.Where(k => Step is < 1 or > 3 || !visible[Step].Contains(k)))
            yield return new("Model." + key, Convert.ToString(typeof(FileJobDraft).GetProperty(key)!.GetValue(this), CultureInfo.InvariantCulture) ?? "");
        if (Step != 3)
            for (var index = 0; index < AdditionalDestinations.Count; index++)
            {
                yield return new($"Model.AdditionalDestinations[{index}].Destination", AdditionalDestinations[index]?.Destination ?? "");
                yield return new($"Model.AdditionalDestinations[{index}].Keep", AdditionalDestinations[index]?.Keep ?? "");
            }
    }
}
