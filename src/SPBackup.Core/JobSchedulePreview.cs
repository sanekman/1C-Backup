namespace SPBackup.Core;

/// <summary>A forecast, never a promise that an executor is running or a backup succeeded.</summary>
public sealed record JobSchedulePreview(string Summary, IReadOnlyList<DateTimeOffset> Runs,
    string TimeZoneId, string? Error = null)
{
    public static JobSchedulePreview Create(BackupJob job, DateTimeOffset now, bool useLocalTime)
    {
        var plan = useLocalTime ? job with { TimeZoneId = BackupSchedule.LocalTimeZone } : job;
        if (plan.ScheduleKind == ScheduleKind.Manual)
            return new("Запуск только вручную. Кнопка «Создать копию» доступна независимо от расписания.", [], plan.TimeZoneId);
        if (!plan.Enabled)
            return new("Автоматические запуски задания выключены. Его можно запустить вручную.", [], plan.TimeZoneId);
        try
        {
            var errors = plan.ValidateSchedule();
            if (errors.Count != 0) return new("Расписание требует исправления.", [], plan.TimeZoneId, string.Join(" ", errors));
            if (plan.ScheduleKind == ScheduleKind.SystemStartup)
                return new("Одна попытка за загрузку системы после запуска агента. Перезапуск окна и выход из сна не повторяют копию. После ошибки можно повторить вручную.", [], plan.TimeZoneId);
            var times = new List<DateTimeOffset>(); var after = now;
            for (var i = 0; i < 3; i++)
            {
                if (BackupSchedule.NextRun(plan, after) is not { } next) break;
                times.Add(next); after = next;
            }
            return new("Ближайшие плановые запуски (это прогноз, а не подтверждение выполнения).", times, plan.TimeZoneId);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or TimeZoneNotFoundException or InvalidTimeZoneException)
        { return new("Расписание требует исправления.", [], plan.TimeZoneId, error.Message); }
    }
}
