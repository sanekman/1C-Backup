using System.Windows;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

public partial class MainWindow
{
    private void UpdateRunGuidance()
    {
        if (OpenHistoryJobButton is null || RunNextStep is null) return;
        var run = RunHistory.SelectedItem as BackupRun;
        var available = run is not null && jobs.Any(job => job.Id == run.JobId);
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
            BackupRunStatus.Succeeded when run.Warnings?.Count > 0 => "Запуск успешен, есть предупреждения. Прочитайте их ниже и проверьте, требуется ли очистка старых копий или рабочих файлов.",
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

    private void HistoryView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (RunDetails is not null) RunDetails.Height = ActualHeight < 650 ? 60 : 120;
    }
}
