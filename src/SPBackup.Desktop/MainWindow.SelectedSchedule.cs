using System.IO;
using System.Windows;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private DateTimeOffset selectedScheduleAt;
    private BackupJob? selectedScheduleJob;
    private AgentMode? selectedScheduleMode;
    private JobSchedulePreview? selectedSchedulePreview;
    private Dictionary<Guid, (BackupJob Job, MissedScheduledRun Missed)> missedSchedules = [];
    private DateTimeOffset missedSchedulesReadAt;
    private string? missedSchedulesProfile;
    private bool missedSchedulesReadFailed;

    private int? ReadCatchUpWindow(ScheduleKind kind) =>
        kind is ScheduleKind.Daily or ScheduleKind.Weekly && CatchUpMissedRun.IsChecked == true
            ? int.TryParse(CatchUpMinutes.Text, out var minutes) ? minutes : 0
            : null;

    private async Task RefreshMissedSchedulesAsync()
    {
        var profile = agentSettings?.Mode == AgentMode.Managed
            ? new ManagedAgentProfile(DesktopPaths.DataDirectory, agentSettings).DirectoryPath : DesktopPaths.DataDirectory;
        var now = DateTimeOffset.UtcNow;
        if (missedSchedulesProfile == profile && now >= missedSchedulesReadAt && now - missedSchedulesReadAt < TimeSpan.FromSeconds(10))
            return;
        missedSchedulesProfile = profile;
        missedSchedulesReadAt = now;
        try
        {
            var jobs = Jobs.Items.OfType<BackupJob>().ToArray();
            var statuses = await LocalBackupDispatcher.ReadMissedRunsAsync(profile, jobs);
            missedSchedules = jobs.Where(job => statuses.ContainsKey(job.Id))
                .ToDictionary(job => job.Id, job => (job, statuses[job.Id]));
            missedSchedulesReadFailed = false;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException
            or TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            missedSchedules = [];
            missedSchedulesReadFailed = true;
            DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Warning, error);
        }
    }

    private void SelectedSchedule_Expanded(object sender, RoutedEventArgs e)
    { UpdateJobActionsLayout(); UpdateSelectedSchedule(); }

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
        if (job.Enabled && job.ScheduleKind is ScheduleKind.Daily or ScheduleKind.Weekly)
        {
            if (missedSchedules.TryGetValue(job.Id, out var state) && ReferenceEquals(state.Job, job))
                lines.Add((state.Missed.CatchUpAttempted ? "Назначен запуск с опозданием · " : "Пропущенный запуск · ")
                    + state.Missed.ScheduledAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm zzz"));
            if (missedSchedulesReadFailed) lines.Add("Сведения о пропущенных запусках недоступны.");
        }
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
