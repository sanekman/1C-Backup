using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SPBackup.Core;

namespace SPBackup.Desktop;

internal static partial class DesktopSmoke
{
    private static void VerifyAutomationOverview(MainWindow window, Action<string> screenshot)
    {
        var width = window.Width; var height = window.Height;
        var now = DateTimeOffset.UtcNow;
        var job = new BackupJob { Name = "Бухгалтерия — ежедневная копия", ScheduleKind = ScheduleKind.Daily, DailyAt = new TimeOnly(23, 0) };
        var host = new LocalAgentState(Guid.NewGuid(), Environment.ProcessId, now.AddMinutes(-1), now, LocalAgentPhase.Running) { CanRunManually = true, RunsSchedules = true };
        var status = (TextBlock)window.FindName("BackgroundStatus");
        var next = (TextBlock)window.FindName("NextAutomaticRun");
        var start = (Button)window.FindName("StartBackgroundButton"); var stop = (Button)window.FindName("StopBackgroundButton");
        var startVisibility = start.Visibility; var stopVisibility = stop.Visibility;
        try
        {
            window.Width = 900; window.Height = 580;
            start.Visibility = Visibility.Collapsed; stop.Visibility = Visibility.Visible;
            window.RenderAutomationOverview([job], new(now, EngineBusy: true, Agent: host, Service: AutomationService.Running), true);
            if (!status.Text.Contains("Служба работает") || !next.Text.Contains("По расписанию:")) throw new InvalidOperationException("Не показаны служба и следующий запуск.");
            window.UpdateLayout();
            var position = next.TranslatePoint(new Point(), window);
            if (!next.IsVisible || position.X < 0 || position.Y + next.ActualHeight > window.ActualHeight)
                throw new InvalidOperationException("Следующий запуск не помещается в компактное окно.");
            screenshot("automation-service-ready");
            window.RenderAutomationOverview([job], new(now, EngineBusy: true, Agent: host), false);
            if (!status.Text.Contains("Автозапуск этой копии выключен")) throw new InvalidOperationException("Не видно, что после перезагрузки фон не запустится.");
            screenshot("automation-background-no-startup");
            window.RenderAutomationOverview([job], new(now, EngineBusy: true, Agent: host with { CanRunManually = false, RunsSchedules = false }), false);
            if (!status.Text.Contains("Разовая", StringComparison.OrdinalIgnoreCase) || !status.Text.Contains("Расписание не включено"))
                throw new InvalidOperationException("Разовая копия ошибочно показывает включённое расписание.");
            screenshot("automation-manual-only");
            window.RenderAutomationOverview([job], new(now, EngineBusy: true, Agent: host with { UpdatedUtc = now.AddMinutes(-1) }), true);
            if (!status.Text.Contains("не подтверждена")) throw new InvalidOperationException("Устаревшее состояние показывает готовность.");
            window.RenderAutomationOverview([job], new(now, WindowScheduler: true), false);
            if (!status.Text.Contains("пока запущена программа")) throw new InvalidOperationException("Не показан режим планировщика окна.");
            window.RenderAutomationOverview([job], new(now, Service: AutomationService.Stopped), false);
            if (!status.Text.Contains("Служба остановлена")) throw new InvalidOperationException("Остановленная служба выглядит работающей.");
        }
        finally { start.Visibility = startVisibility; stop.Visibility = stopVisibility; window.RenderAutomationOverview([], new(DateTimeOffset.UtcNow), false); window.Width = width; window.Height = height; window.UpdateLayout(); }
    }

    private static void VerifyAutostartOwnership()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true") return;
        const string path = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string name = "OneCBackupAgent";
        using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
        var previous = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var kind = previous is null ? RegistryValueKind.String : key.GetValueKind(name);
        try
        {
            var foreign = "\"C:\\SP-Backup-foreign\\SPBackup.Desktop.exe\" --tray --start-agent";
            key.SetValue(name, foreign);
            if (CurrentUserAgentStartup.IsEnabled()) throw new InvalidOperationException("Чужой автозапуск показан как свой.");
            CurrentUserAgentStartup.Save(false);
            if (!Equals(key.GetValue(name), foreign)) throw new InvalidOperationException("Отключение стёрло чужую запись автозапуска.");
            var rejected = false;
            try { CurrentUserAgentStartup.Save(true); } catch (InvalidOperationException) { rejected = true; }
            if (!rejected || !Equals(key.GetValue(name), foreign)) throw new InvalidOperationException("Включение заменило чужой автозапуск.");
            var legacy = $"\"{System.IO.Path.Combine(AppContext.BaseDirectory, "OneCBackup.Desktop.exe")}\" --tray --start-agent";
            key.SetValue(name, legacy);
            if (!CurrentUserAgentStartup.IsEnabled()) throw new InvalidOperationException("Собственный старый автозапуск не распознан.");
            CurrentUserAgentStartup.UpgradeOwnedLegacyEntry();
            if (!Equals(key.GetValue(name), new AgentAutostartCommands(AppContext.BaseDirectory).Current)) throw new InvalidOperationException("Свой автозапуск не перенесён.");
            CurrentUserAgentStartup.Save(false);
            if (key.GetValue(name) is not null) throw new InvalidOperationException("Свой автозапуск остался после отключения.");
        }
        finally
        {
            if (previous is null) key.DeleteValue(name, false);
            else key.SetValue(name, previous, kind);
        }
    }
}
