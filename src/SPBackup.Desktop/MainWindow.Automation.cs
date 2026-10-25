using System.Windows;
using System.Windows.Controls;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private bool automationReadFailed;
    private AutomationService automationService;
    private bool automationAutostart;
    private bool automationAutostartUnknown;
    private DateTimeOffset automationPlanAt;
    private BackupJob[] automationPlanJobs = [];
    private AutomaticBackupPlan? automationPlan;

    private void AutomationWindow_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateAutomationLayout();

    private void UpdateAutomationLayout()
    {
        if (NextAutomaticRun is null || BackgroundStatus is null) return;
        var compact = ActualHeight < 650 && (CopiesView?.Visibility == Visibility.Visible || HistoryView?.Visibility == Visibility.Visible);
        NextAutomaticRun.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        BackgroundStatus.TextWrapping = compact ? TextWrapping.NoWrap : TextWrapping.Wrap;
        BackgroundStatus.TextTrimming = compact ? TextTrimming.CharacterEllipsis : TextTrimming.None;
    }

    private void UpdateAutomationOverview()
    {
        if (NextAutomaticRun is null) return;
        if (!ready) { ShowAutomationNotice("Проверка состояния…", ""); return; }
        if (!canSave) { ShowAutomationNotice("Настройки не удалось загрузить. Проверьте сообщение внизу окна.", "Расписание не подтверждено."); return; }
        if (agentSettings?.Mode == AgentMode.Managed)
        {
            ShowAutomationNotice("Управление с сервера ещё разрабатывается. Для копий выберите автономный режим.",
                "Настройки → Подключения → Режим работы агента.");
            return;
        }
        var observation = new AutomationObservation(DateTimeOffset.UtcNow, backgroundBusy, backgroundState,
            schedulingTask is not null, schedulerStopping || trayStopping, backgroundStarting, automationService, automationReadFailed, activeRun is not null);
        if (automationPlan is null || observation.Now < automationPlanAt || observation.Now - automationPlanAt >= TimeSpan.FromSeconds(2)
            || jobs.Count != automationPlanJobs.Length || !jobs.Select((job, index) => ReferenceEquals(job, automationPlanJobs[index])).All(same => same))
        {
            automationPlan = BackupAutomationReadiness.Evaluate(jobs, observation).Plan;
            automationPlanJobs = jobs.ToArray(); automationPlanAt = observation.Now;
        }
        ApplyAutomationOverview(new(BackupAutomationReadiness.DetermineExecution(observation), automationPlan), observation,
            automationAutostart, automationAutostartUnknown);
    }

    private void ShowAutomationNotice(string message, string detail)
    {
        BackgroundStatus.Text = message; NextAutomaticRun.Text = detail;
        BackgroundStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        BackgroundStatus.ToolTip = NextAutomaticRun.ToolTip = message + "\n" + detail;
    }

    internal void RenderAutomationOverview(IReadOnlyList<BackupJob> source, AutomationObservation observation, bool autostart, bool autostartUnknown = false)
    {
        ApplyAutomationOverview(BackupAutomationReadiness.Evaluate(source, observation), observation, autostart, autostartUnknown);
    }

    private void ApplyAutomationOverview(BackupAutomationReadiness state, AutomationObservation observation, bool autostart, bool autostartUnknown)
    {
        var count = state.Plan.TimedJobs + state.Plan.StartupJobs;
        var label = state.Execution switch {
            AutomationExecution.Service => "Служба работает — до входа в Windows и после закрытия программы.",
            AutomationExecution.Background => autostartUnknown ? "Фоновый агент работает. Автозапуск проверить не удалось."
                : autostart ? "Фоновый агент работает; автозапуск при входе включён."
                : "Фоновый агент работает. Автозапуск этой копии выключен.",
            AutomationExecution.Window => "Расписание работает, пока запущена программа.",
            AutomationExecution.Starting => "Автоматическое копирование включается…",
            AutomationExecution.Stopping => "Копирование останавливается…",
            AutomationExecution.OneRun => "Идёт разовая копия. Расписание не включено.",
            AutomationExecution.Busy => "Профиль занят. Работа расписания пока не подтверждена.",
            AutomationExecution.ServiceNotReady => "Служба запущена, но агент ещё не подтвердил готовность.",
            AutomationExecution.Faulted => "Агент остановлен с ошибкой. Откройте историю и включите его снова.",
            AutomationExecution.Unavailable => "Не удалось проверить агент. Работа расписания не подтверждена.",
            _ when observation.Service == AutomationService.Stopped => "Служба остановлена. Нажмите «Включить автоматически».",
            _ when observation.Service == AutomationService.OtherProfile => "Служба настроена для другого профиля. Проверьте «Фоновая работа».",
            _ when observation.Service == AutomationService.Unavailable => "Не удалось проверить службу. Откройте «Фоновая работа».",
            _ => "Расписание выключено. Нажмите «Включить автоматически»."
        };
        if (state.ScheduleRunning && count == 0) label += " Заданий по расписанию нет.";
        BackgroundStatus.Text = label;
        BackgroundStatus.SetResourceReference(TextBlock.ForegroundProperty,
            state.ScheduleRunning && count > 0 && state.Plan.InvalidJobs == 0 ? "SuccessBrush" : "TextBrush");
        var next = state.Plan.NextRunUtc is { } time
            ? $"По расписанию: {time.ToLocalTime():dd.MM HH:mm} — {state.Plan.NextJobName}"
            : state.Plan.StartupJobs > 0 ? $"При запуске системы: {state.Plan.StartupJobs} заданий."
            : state.Plan.DisabledJobs > 0 ? $"Расписания заданий выключены: {state.Plan.DisabledJobs}."
            : "Задания можно запускать вручную.";
        if (state.Plan.InvalidJobs > 0) next = $"Проверьте расписание: {state.Plan.InvalidJobs} заданий с ошибкой.";
        var detail = label + "\n" + next;
        if (state.Plan.StartupJobs > 0 && state.Plan.NextRunUtc is not null)
            detail += $"\nПри запуске системы: ещё {state.Plan.StartupJobs} заданий.";
        if (state.Execution == AutomationExecution.Background)
            detail += autostartUnknown ? "\nНе удалось проверить автозапуск при входе в Windows."
                : autostart ? "\nПосле перезагрузки агент запустится при входе этого пользователя. До входа нужна служба Windows."
                : "\nПосле перезагрузки агент нужно включить снова. Для запуска при входе настройте автозапуск; до входа — службу Windows.";
        if (state.Execution == AutomationExecution.Window) detail += "\nПри полном выходе расписание остановится. Сворачивание в трей его не выключает.";
        detail += "\nВремя местное. Компьютер должен быть включён и не спать. Это план запуска, а не проверка доступа к источникам и хранилищам.";
        var warning = observation.Agent?.Warning ?? observation.Agent?.Error ?? observation.Agent?.MonitoringError;
        if (state.Execution is not (AutomationExecution.Background or AutomationExecution.Service or AutomationExecution.Faulted or AutomationExecution.OneRun)) warning = null;
        NextAutomaticRun.Text = next;
        if (!string.IsNullOrWhiteSpace(warning))
        {
            detail += "\nПоследнее сообщение агента: " + warning;
            NextAutomaticRun.Text = "Сообщение агента: " + warning;
            if (state.ScheduleRunning) BackgroundStatus.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        }
        BackgroundStatus.ToolTip = detail;
        NextAutomaticRun.ToolTip = detail;
    }
}
