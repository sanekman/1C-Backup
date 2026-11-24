using System.Globalization;
using System.Windows;
using System.Windows.Data;
using SPBackup.Core;

namespace SPBackup.Desktop;

public sealed class RunDeliveryConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is BackupRun run
        ? run.Destinations switch
        {
            { Count: 1 } targets => targets[0].Destination,
            { Count: > 1 } targets => $"Сохранено в {targets.Count(target => target.Record is not null)} из {targets.Count}",
            _ => run.Backup?.Location ?? run.Error ?? "—"
        } : "—";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public partial class MainWindow
{
    private void UpdateRunGuidance()
    {
        if (OpenHistoryJobButton is null || RunNextStep is null) return;
        var run = RunHistory.SelectedItem as BackupRun;
        var available = run is not null && jobs.Any(job => job.Id == run.JobId);
        OpenHistoryCopyButton.Visibility = run?.Backup is not null || run?.Destinations?.Any(item => item.Record is not null) == true
            ? Visibility.Visible : Visibility.Collapsed;
        OpenHistoryCopyButton.IsEnabled = ready && !editorOpen && !persisting && !copiesRefreshing;
        OpenHistoryJobButton.IsEnabled = ready && !editorOpen && !persisting && available;
        OpenHistoryJobButton.ToolTip = run is null ? "Выберите запуск в таблице."
            : !available ? "Задание сейчас недоступно. История запуска сохранена."
            : editorOpen || persisting ? "Сначала сохраните или отмените настройку задания."
            : "Открыть текущее задание выбранного запуска.";
        RunNextStep.Visibility = run is null ? Visibility.Collapsed : Visibility.Visible;
        RunNextStep.Text = run?.Status switch
        {
            BackupRunStatus.Failed => "Запуск завершился с ошибкой. Прочитайте причину ниже и проверьте настройки задания перед повтором.",
            BackupRunStatus.PartiallySucceeded => "Копия сохранена в части хранилищ. Проверьте ошибки доставки ниже. Повторный запуск создаст новую копию для всех хранилищ.",
            BackupRunStatus.Cancelled => "Запуск отменён. Уже доставленные копии сохранены; рабочая папка может содержать незавершённые файлы. Проверьте результат перед повтором.",
            BackupRunStatus.Interrupted => "Завершение запуска не записано. Проверьте хранилища и рабочую папку перед повтором.",
            BackupRunStatus.Succeeded when run.Warnings?.Count > 0 => "Копия создана с предупреждениями. Прочитайте их ниже: могли быть пропущены файлы или не выполнена очистка старых копий. Устраните причину и повторите копирование при необходимости.",
            BackupRunStatus.Succeeded => "Запуск завершён успешно. Проверка файла доступна в разделе «Резервные копии»; возможность восстановления проверяйте отдельно.",
            BackupRunStatus.Running => "Запуск ещё не имеет результата. Нажмите «Обновить» или дождитесь завершения.",
            _ => ""
        };
        if (run is not null && !available) RunNextStep.Text += " Задание сейчас недоступно; история сохранена.";
    }

    private void OpenHistoryJob_Click(object sender, RoutedEventArgs e)
    {
        if (!ready || editorOpen || persisting || !OpenHistoryJobButton.IsEnabled || RunHistory.SelectedItem is not BackupRun run) return;
        var job = jobs.FirstOrDefault(job => job.Id == run.JobId);
        if (job is null) { UpdateRunGuidance(); return; }
        JobSearch.Text = "";
        ShowPage("jobs");
        Jobs.SelectedItem = job;
        Jobs.ScrollIntoView(job);
        Status.Text = $"Открыто текущее задание «{job.Name}».";
    }

    private async void OpenHistoryCopy_Click(object sender, RoutedEventArgs e)
    {
        if (!OpenHistoryCopyButton.IsEnabled || RunHistory.SelectedItem is not BackupRun run) return;
        await RefreshCopiesAsync();
        if (RunHistory.SelectedItem is not BackupRun current || current.RunId != run.RunId || !HistoryView.IsVisible) return;

        var ids = run.Destinations?.Where(item => item.Record is not null).Select(item => item.Record!.Id).ToHashSet();
        var copy = copyRows.FirstOrDefault(row => row.Record.JobId == run.JobId
            && (ids is { Count: > 0 } ? ids.Contains(row.Record.Id)
                : run.Backup is { } backup && row.Record.Backup.Location == backup.Location
                    && row.Record.Backup.Length == backup.Length && row.Record.Backup.Sha256 == backup.Sha256));
        if (copy is null)
        {
            Status.Text = "Копия этого запуска больше не найдена в каталоге.";
            return;
        }

        ShowPage("copies");
        SelectCopiesForJob(run.JobId);
        Copies.SelectedItem = copy;
        Copies.ScrollIntoView(copy);
        Copies.Focus();
    }

    private void HistoryView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (RunDetails is not null) RunDetails.Height = ActualHeight < 650 ? 60 : 120;
    }
}
