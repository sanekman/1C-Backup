namespace SPBackup.Core;

public enum ScheduleKind { Manual, Daily, Weekly, SystemStartup }

[Flags]
public enum WeekDays { None = 0, Monday = 1, Tuesday = 2, Wednesday = 4, Thursday = 8, Friday = 16, Saturday = 32, Sunday = 64 }

public static class BackupSchedule
{
    public const string LocalTimeZone = "Local";

    public static TimeZoneInfo ResolveTimeZone(string id)
    {
        if (id != LocalTimeZone) return TimeZoneInfo.FindSystemTimeZoneById(id);
        // The standalone host can stay running while Windows time zone settings change.
        TimeZoneInfo.ClearCachedData();
        return TimeZoneInfo.Local;
    }

    public static WeekDays DayFlag(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => WeekDays.Monday, DayOfWeek.Tuesday => WeekDays.Tuesday,
        DayOfWeek.Wednesday => WeekDays.Wednesday, DayOfWeek.Thursday => WeekDays.Thursday,
        DayOfWeek.Friday => WeekDays.Friday, DayOfWeek.Saturday => WeekDays.Saturday,
        DayOfWeek.Sunday => WeekDays.Sunday, _ => throw new ArgumentOutOfRangeException(nameof(day))
    };

    /// <summary>Strictly after the supplied instant. A repeated DST hour runs only once.</summary>
    public static DateTimeOffset? NextRun(BackupJob job, DateTimeOffset after)
    {
        var errors = job.ValidateSchedule();
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(job));
        if (!job.Enabled || job.ScheduleKind is ScheduleKind.Manual or ScheduleKind.SystemStartup) return null;
        var zone = ResolveTimeZone(job.TimeZoneId);
        var firstDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, zone).DateTime);
        for (var offset = 0; offset <= 8; offset++)
        {
            var date = firstDate.AddDays(offset);
            if (job.ScheduleKind == ScheduleKind.Weekly && !job.DaysOfWeek.HasFlag(DayFlag(date.DayOfWeek))) continue;
            DateTimeOffset? earliest = null;
            foreach (var time in job.ScheduledTimes)
            {
                var local = date.ToDateTime(time, DateTimeKind.Unspecified);
                // When clocks jump forward, move to the first valid minute on that date.
                while (zone.IsInvalidTime(local) && DateOnly.FromDateTime(local) == date) local = local.AddMinutes(1);
                if (DateOnly.FromDateTime(local) != date) continue;
                var utc = zone.IsAmbiguousTime(local)
                    ? new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).ToUniversalTime()
                    : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
                if (utc > after && (earliest is null || utc < earliest)) earliest = utc;
            }
            if (earliest is not null) return earliest;
        }
        throw new InvalidOperationException("Не удалось определить следующий запуск.");
    }
}
