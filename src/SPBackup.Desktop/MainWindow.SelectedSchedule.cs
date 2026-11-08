using System.Windows;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private DateTimeOffset selectedScheduleAt;
    private BackupJob? selectedScheduleJob;
    private AgentMode? selectedScheduleMode;
    private JobSchedulePreview? selectedSchedulePreview;

    private void SelectedSchedule_Expanded(object sender, RoutedEventArgs e) => UpdateSelectedSchedule();

    private void UpdateSelectedSchedule()
    {
        if (SelectedSchedulePanel is null) return;
        var job = Jobs.SelectedItem as BackupJob;
        SelectedSchedulePanel.Visibility = job is null ? Visibility.Collapsed : Visibility.Visible;
        if (job is null) return;
        SelectedSchedulePanel.Header = job.ScheduleKind == ScheduleKind.Manual ? "Ручное задание — как запускать"
            : job.Enabled ? "Расписание выбранного задания" : "Расписание задания выключено";
        EditSelectedScheduleButton.IsEnabled = EditJobButton.IsEnabled;
        EditSelectedScheduleButton.ToolTip = EditJobButton.ToolTip;
        if (!SelectedSchedulePanel.IsExpanded) return;
        var now = DateTimeOffset.UtcNow;
        if (!ReferenceEquals(job, selectedScheduleJob) || agentSettings?.Mode != selectedScheduleMode
            || selectedSchedulePreview is null || now < selectedScheduleAt || now - selectedScheduleAt >= TimeSpan.FromSeconds(30)
            || selectedSchedulePreview.Runs.Count > 0 && selectedSchedulePreview.Runs[0] <= now)
        {
            selectedSchedulePreview = JobSchedulePreview.Create(job, now, agentSettings?.Mode != AgentMode.Managed);
            selectedScheduleJob = job; selectedScheduleMode = agentSettings?.Mode; selectedScheduleAt = now;
        }
        var preview = selectedSchedulePreview;
        var lines = new List<string> { "Задание: " + job.Name, preview.Summary };
        if (preview.Error is { } error) lines.Add(error);
        if (preview.Runs.Count > 0)
        {
            lines.Add("Время на этом компьютере: " + TimeZoneInfo.Local.DisplayName);
            lines.AddRange(preview.Runs.Select(time => time.ToLocalTime().ToString("dd.MM.yyyy HH:mm zzz")));
        }
        lines.Add(agentSettings?.Mode == AgentMode.Managed ? "Пояс расписания задан сервером: " + preview.TimeZoneId : "Расписание использует часовой пояс Windows этого компьютера.");
        var execution = CurrentScheduleExecution();
        lines.Add(backgroundBusy && !BackgroundExecutionMatchesMode ? "Работает агент другого режима. Остановите его и включите снова."
            : execution switch
            {
                AutomationExecution.Service => "Исполнитель: служба Windows. Работа возможна до входа пользователя.",
                AutomationExecution.Background => "Исполнитель: фоновый агент. После перезагрузки нужен вход пользователя или служба Windows.",
                AutomationExecution.Window => "Исполнитель: окно программы. Полный выход останавливает расписание.",
                AutomationExecution.Starting => "Исполнитель запускается. Дождитесь готовности.",
                AutomationExecution.Stopping => "Исполнитель останавливается. Запуски пока не подтверждены.",
                AutomationExecution.OneRun => "Идёт разовая копия. Это не подтверждает работу расписания.",
                AutomationExecution.Off => "Автоматический исполнитель выключен. Для расписания нажмите «Включить автоматически».",
                _ => "Работа автоматического исполнителя пока не подтверждена. Проверьте его состояние в настройках."
            });
        lines.Add("Компьютер должен быть включён и не спать. Занятый исполнитель может начать копию позже планового времени. Результат проверяйте в истории.");
        SelectedScheduleDetails.Text = string.Join("\n\n", lines);
    }

    private AutomationExecution CurrentScheduleExecution()
    {
        if (backgroundBusy && !BackgroundExecutionMatchesMode) return AutomationExecution.Busy;
        return BackupAutomationReadiness.DetermineExecution(new(DateTimeOffset.UtcNow, backgroundBusy, backgroundState,
            schedulingTask is not null, schedulerStopping || trayStopping, backgroundStarting, automationService, automationReadFailed, activeRun is not null));
    }

    private void EditSelectedSchedule_Click(object sender, RoutedEventArgs e)
    { if (EditSelectedScheduleButton.IsEnabled) EditJob_Click(sender, e); }
}
