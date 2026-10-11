using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

internal static partial class DesktopSmoke
{
    private static Task VerifyJobOverviewAsync(MainWindow window, Action<string> screenshot)
    {
        var grid = (DataGrid)window.FindName("Jobs");
        var savedItems = grid.ItemsSource;
        var savedSelection = grid.SelectedItem;
        var savedOverview = window.LatestJobRuns;
        var savedSearch = ((TextBox)window.FindName("JobSearch")).Text;
        ((Button)window.FindName("JobsNavigation")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        try
        {
            ((TextBox)window.FindName("JobSearch")).Text = "";
            var jobs = Enumerable.Range(0, 8).Select(index => new BackupJob { Id = Guid.NewGuid(),
                Name = "Одинаковое название", Source = "fixture", DestinationDirectory = "D:\\Fixture" }).ToArray();
            var now = DateTimeOffset.UtcNow;
            BackupRun Run(int index, BackupRunStatus status) => new(Guid.NewGuid(), jobs[index].Id, "Историческое название",
                now.AddMinutes(-index), status == BackupRunStatus.Running ? null : now, status, "fixture",
                Error: status == BackupRunStatus.Failed ? "Ошибка только первой базы" : null);
            var runs = new[] { Run(0, BackupRunStatus.Failed), Run(1, BackupRunStatus.PartiallySucceeded),
                Run(2, BackupRunStatus.Succeeded) with { Warnings = ["Не удалена старая копия"] },
                Run(4, BackupRunStatus.Running), Run(5, BackupRunStatus.Cancelled), Run(6, BackupRunStatus.Interrupted),
                Run(7, BackupRunStatus.Succeeded), Run(0, BackupRunStatus.Succeeded) with { StartedUtc = now.AddDays(-1) } };
            var overview = JobRunOverview.From(runs.Reverse()); // Input ordering must not decide the latest run.
            grid.ItemsSource = jobs;
            grid.SelectedItem = jobs[1];
            window.LatestJobRuns = overview;
            window.UpdateLayout();
            var column = grid.Columns.Single(column => Equals(column.Header, "Последний запуск"));
            JobRunBadge Cell(int index)
            {
                grid.ScrollIntoView(jobs[index], column);
                window.UpdateLayout();
                var element = column.GetCellContent(jobs[index]);
                var content = element is null ? null : FindDescendant<ContentControl>(element);
                return content?.Content as JobRunBadge ?? throw new InvalidOperationException("Последний запуск не привязался к строке задания.");
            }
            var labels = new[] { "Ошибка", "Частично", "Предупреждения", "Не запускалось", "Выполняется", "Отменено", "Прервано", "Успешно" };
            for (var index = 0; index < jobs.Length; index++)
            {
                var cell = Cell(index);
                if (cell.Label != labels[index]) throw new InvalidOperationException("Результаты одинаково названных заданий перепутаны: " + cell.Label);
                if (index != 3 && !cell.Help.Contains("Начало: ")) throw new InvalidOperationException("Результат не показывает время в подсказке.");
                if (cell.Color == "#15803D" && index != 7) throw new InvalidOperationException("Ошибка или предупреждение показаны зелёным успехом.");
            }
            if (!Cell(0).Help.Contains("Ошибка только первой базы") || !Cell(2).Help.Contains("Не удалена старая копия"))
                throw new InvalidOperationException("Подсказка потеряла ошибку или предупреждение.");
            window.LatestJobRuns = JobRunOverview.Unavailable;
            if (Cell(7).Label != "Нет данных" || !ReferenceEquals(grid.SelectedItem, jobs[1]))
                throw new InvalidOperationException("Ошибка чтения оставила ложный успех или сбросила выбор.");
            window.LatestJobRuns = JobRunOverview.Loading;
            if (Cell(0).Label != "Загрузка…") throw new InvalidOperationException("Загрузка выдана за отсутствие запусков.");
            window.LatestJobRuns = JobRunOverview.From([]);
            if (Cell(0).Label != "Не запускалось") throw new InvalidOperationException("Пустая история оставила прежний результат.");
            window.LatestJobRuns = overview;
            grid.ScrollIntoView(jobs[0]);
            window.UpdateLayout();
            var scroll = FindDescendant<ScrollViewer>(grid);
            if (scroll is null || scroll.ExtentWidth > scroll.ViewportWidth + 1)
                throw new InvalidOperationException("Таблица заданий требует горизонтальной прокрутки в компактном окне.");
            if (!ReferenceEquals(grid.SelectedItem, jobs[1])) throw new InvalidOperationException("Обновление результатов сбросило выбор задания.");
            screenshot("jobs-latest-results-compact");
        }
        finally
        {
            grid.ItemsSource = savedItems;
            window.LatestJobRuns = savedOverview;
            ((TextBox)window.FindName("JobSearch")).Text = savedSearch;
            grid.SelectedItem = savedSelection;
            window.UpdateLayout();
        }
        return Task.CompletedTask;
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } descendant) return descendant;
        }
        return null;
    }
}
