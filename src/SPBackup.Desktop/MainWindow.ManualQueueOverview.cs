using System.Windows;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private readonly HashSet<Guid> manualReceiptWarnings = [];

    private void UpdateManualQueueOverview()
    {
        if (ManualQueuePanel is null) return;
        var background = backgroundManualRuns.Values.Where(request => request.SessionId == backgroundState?.SessionId).ToArray();
        var count = localManualRuns.Count + background.Length;
        var restoring = BackgroundManualSessionLive && !BackgroundAcceptsManualRuns;
        ManualQueuePanel.Visibility = count > 0 || restoring ? Visibility.Visible : Visibility.Collapsed;
        ManualQueuePanel.Header = restoring ? "Проверяется очередь ручных запусков…" : $"Принятые ручные запуски: {count}";
        string Name(Guid id) => jobs.FirstOrDefault(job => job.Id == id)?.Name ?? "Задание недоступно в текущем списке";
        var lines = new List<string>();
        if (restoring) lines.Add("Восстанавливаются подтверждения агента. Повторный запуск пока недоступен.");
        foreach (var id in localManualRuns.Keys.Take(20))
            lines.Add(Name(id) + (activeJobId == id ? " — выполняется в окне" : " — принято окном, ожидает выполнения"));
        foreach (var request in background.Take(Math.Max(0, 20 - lines.Count)))
            lines.Add(Name(request.JobId) + (manualReceiptWarnings.Contains(request.RequestId)
                ? " — подтверждение недоступно; проверьте историю перед повтором"
                : " — передано агенту, ожидается итоговый результат"));
        if (count > 20) lines.Add("Показаны первые 20 запросов. Всего: " + count + ".");
        lines.Add("Это ручные запросы, известные этому окну. Здесь не показана полная очередь расписания. Порядок строк не означает порядок выполнения. Перед повторным запуском проверьте историю.");
        ManualQueueDetails.Text = string.Join("\n", lines);
    }
}
