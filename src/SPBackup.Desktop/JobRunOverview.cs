using System.Globalization;
using System.Windows;
using System.Windows.Data;
using SPBackup.Core;

namespace SPBackup.Desktop;

public sealed record JobRunOverview(IReadOnlyDictionary<Guid, BackupRun>? Runs, bool IsLoading = false)
{
    public static JobRunOverview Loading { get; } = new(null, true);
    public static JobRunOverview Unavailable { get; } = new(null, false);
    internal IReadOnlyDictionary<Guid, BackupRun> Delivered { get; init; } = new Dictionary<Guid, BackupRun>();

    public static JobRunOverview From(IEnumerable<BackupRun> runs)
    {
        var groups = runs.OrderByDescending(run => run.StartedUtc).ThenByDescending(run => run.RunId)
            .GroupBy(run => run.JobId).ToArray();
        return new(groups.ToDictionary(group => group.Key, group => group.First()))
        {
            Delivered = groups.Select(group => group.Where(run => run.FinishedUtc is not null &&
                    (run.Destinations is { } targets ? targets.Any(target => target.Record is not null) : run.Backup is not null))
                .MaxBy(run => run.FinishedUtc))
                .OfType<BackupRun>().ToDictionary(run => run.JobId)
        };
    }

    internal static string DestinationDetails(DestinationResult target) => target.Destination + " — "
        + (target.Record is { } saved ? "Сохранено · " + saved.Backup.Location
            : "Доставка не подтверждена" + (string.IsNullOrWhiteSpace(target.Error) ? "" : " · " + target.Error))
        + (target.Warnings is { Count: > 0 } warnings ? " · " + string.Join(" · ", warnings) : "");
}

public sealed record JobRunBadge(string Label, string Color, string Help);

public sealed class JobRunOverviewConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (parameter is "copy")
        {
            if (values.Length != 2 || values[0] is not BackupJob copyJob || values[1] is not JobRunOverview copyOverview)
                return new JobRunBadge("—", "#64748B", "");
            if (copyOverview.IsLoading || copyOverview.Runs is null)
                return new JobRunBadge(copyOverview.IsLoading ? "Загрузка…" : "Нет данных", "#64748B", "История ещё недоступна.");
            if (!copyOverview.Delivered.TryGetValue(copyJob.Id, out var delivered))
                return new JobRunBadge("—", "#64748B", "В истории нет подтверждённой доставки.");
            var locations = delivered.Destinations is { } targets
                ? targets.Where(target => target.Record is not null).Select(target => target.Record!.Backup.Location)
                : new[] { delivered.Backup!.Location };
            var date = delivered.FinishedUtc!.Value.ToLocalTime();
            return new JobRunBadge(date.ToString("dd.MM HH:mm", culture), "#64748B",
                "Последний завершённый запуск с подтверждённой доставкой · " + date.ToString("dd.MM.yyyy HH:mm", culture)
                + "\n" + string.Join("\n", locations) + "\nНаличие файла сейчас можно проверить в разделе «Резервные копии».");
        }
        if (values.Length == 2 && values[0] is ExternalBackupTask task)
            return new JobRunBadge(task.ResultDescription, task.Poll?.Error is not null || task.Latest?.Observation.Outcome == ExternalBackupOutcome.Failed
                ? "#B91C1C" : task.FreshnessWarning ? "#B45309" : task.Latest is null ? "#64748B" : "#15803D", task.ResultDetails);
        var identity = values.Length == 2 && values[0] is BackupJob job ? job.Id : values.FirstOrDefault();
        if (values.Length != 2 || identity is not Guid id || values[1] is not JobRunOverview overview || overview.IsLoading)
            return new JobRunBadge("Загрузка…", "#64748B", "Загружается история запусков.");
        if (overview.Runs is null)
            return new JobRunBadge("Нет данных", "#B45309", "Не удалось прочитать историю. Обновите её в разделе «История запусков»; прежний результат не подтверждён.");
        if (!overview.Runs.TryGetValue(id, out var run))
            return new JobRunBadge("Не запускалось", "#64748B", "Для этого задания нет записей в истории. Создайте пробную копию, чтобы проверить настройки.");
        var warnings = run.Status == BackupRunStatus.Succeeded && run.Warnings?.Count > 0;
        var label = run.Status == BackupRunStatus.PartiallySucceeded && run.Destinations is { } destinations
            ? $"Частично · {destinations.Count(target => target.Record is not null)} из {destinations.Count}"
            : warnings ? "Предупреждения" : run.StatusDescription;
        var color = run.Status switch
        {
            BackupRunStatus.Succeeded when !warnings => "#15803D",
            BackupRunStatus.Running => "#1D4ED8",
            BackupRunStatus.Failed => "#B91C1C",
            _ => "#B45309"
        };
        var help = label + "\nНачало: " + run.StartedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", culture);
        if (run.FinishedUtc is { } finished) help += "\nЗавершение: " + finished.ToLocalTime().ToString("dd.MM.yyyy HH:mm", culture);
        if (run.Destinations is { Count: > 0 } deliveries)
            help += "\n" + string.Join("\n", deliveries.Select(JobRunOverview.DestinationDetails));
        if (!string.IsNullOrWhiteSpace(run.Details)) help += "\n" + run.Details;
        help += "\nВыберите задание и нажмите «Результат», чтобы открыть последний запуск. Проверка запуска не заменяет пробное восстановление базы.";
        return new JobRunBadge(label, color, help);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class JobNextRunConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.FirstOrDefault() is ExternalBackupTask task)
            return task.Enabled ? task.ScheduleDescription : "Выключен";
        if (values.Length != 2 || values[0] is not BackupJob job || values[1] is not DateTimeOffset now) return "—";
        if (job.ScheduleKind == ScheduleKind.Manual) return "Вручную";
        if (!job.Enabled) return "Выключен";
        if (job.ScheduleKind == ScheduleKind.SystemStartup) return "При запуске";
        try { return BackupSchedule.NextRun(job, now)?.ToLocalTime().ToString("dd.MM HH:mm", culture) ?? "—"; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return "Проверьте план"; }
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public partial class MainWindow
{
    public static readonly DependencyProperty JobScheduleTimeProperty = DependencyProperty.Register(
        nameof(JobScheduleTime), typeof(DateTimeOffset), typeof(MainWindow), new PropertyMetadata(default(DateTimeOffset)));

    public DateTimeOffset JobScheduleTime
    {
        get => (DateTimeOffset)GetValue(JobScheduleTimeProperty);
        private set => SetValue(JobScheduleTimeProperty, value);
    }

    private void UpdateJobScheduleTime()
    {
        var now = DateTimeOffset.UtcNow;
        var minute = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMinute));
        if (JobScheduleTime != minute) JobScheduleTime = minute;
    }

    public static readonly DependencyProperty LatestJobRunsProperty = DependencyProperty.Register(
        nameof(LatestJobRuns), typeof(JobRunOverview), typeof(MainWindow), new PropertyMetadata(JobRunOverview.Loading));
    public JobRunOverview LatestJobRuns
    {
        get => (JobRunOverview)GetValue(LatestJobRunsProperty);
        set => SetValue(LatestJobRunsProperty, value);
    }
}
