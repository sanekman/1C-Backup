using System.Globalization;
using System.Windows;
using System.Windows.Data;
using SPBackup.Core;

namespace SPBackup.Desktop;

public sealed record JobRunOverview(IReadOnlyDictionary<Guid, BackupRun>? Runs, bool IsLoading = false)
{
    public static JobRunOverview Loading { get; } = new(null, true);
    public static JobRunOverview Unavailable { get; } = new(null, false);
    public static JobRunOverview From(IEnumerable<BackupRun> runs) => new(runs
        .OrderByDescending(run => run.StartedUtc).ThenByDescending(run => run.RunId)
        .GroupBy(run => run.JobId).ToDictionary(group => group.Key, group => group.First()));
}

public sealed record JobRunBadge(string Label, string Color, string Help);

public sealed class JobRunOverviewConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 2 || values[0] is not Guid id || values[1] is not JobRunOverview overview || overview.IsLoading)
            return new JobRunBadge("Загрузка…", "#64748B", "Загружается история запусков.");
        if (overview.Runs is null)
            return new JobRunBadge("Нет данных", "#B45309", "Не удалось прочитать историю. Обновите её в разделе «История запусков»; прежний результат не подтверждён.");
        if (!overview.Runs.TryGetValue(id, out var run))
            return new JobRunBadge("Не запускалось", "#64748B", "Для этого задания нет записей в истории. Создайте пробную копию, чтобы проверить настройки.");
        var warnings = run.Status == BackupRunStatus.Succeeded && run.Warnings?.Count > 0;
        var label = warnings ? "Предупреждения" : run.StatusDescription;
        var color = run.Status switch
        {
            BackupRunStatus.Succeeded when !warnings => "#15803D",
            BackupRunStatus.Running => "#1D4ED8",
            BackupRunStatus.Failed => "#B91C1C",
            _ => "#B45309"
        };
        var help = label + "\nНачало: " + run.StartedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", culture);
        if (run.FinishedUtc is { } finished) help += "\nЗавершение: " + finished.ToLocalTime().ToString("dd.MM.yyyy HH:mm", culture);
        if (!string.IsNullOrWhiteSpace(run.Details)) help += "\n" + run.Details;
        help += "\nВыберите задание и нажмите «Результат», чтобы открыть последний запуск. Проверка запуска не заменяет пробное восстановление базы.";
        return new JobRunBadge(label, color, help);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public partial class MainWindow
{
    public static readonly DependencyProperty LatestJobRunsProperty = DependencyProperty.Register(
        nameof(LatestJobRuns), typeof(JobRunOverview), typeof(MainWindow), new PropertyMetadata(JobRunOverview.Loading));
    public JobRunOverview LatestJobRuns
    {
        get => (JobRunOverview)GetValue(LatestJobRunsProperty);
        set => SetValue(LatestJobRunsProperty, value);
    }
}
