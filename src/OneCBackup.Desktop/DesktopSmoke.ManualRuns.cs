using System.IO;
using System.Windows;
using System.Windows.Controls;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

internal static partial class DesktopSmoke
{
    private sealed class WaitingManualExecutor : IBackupExecutor
    {
        public async Task<string> CreateAsync(BackupJob job, string workDirectory, CancellationToken token)
        { await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException("Unreachable"); }
    }

    private static async Task VerifyManualQueueReopenAsync(MainWindow window, Action<string, Window> screenshot)
    {
        var store = new JobStore(Path.Combine(DesktopPaths.DataDirectory, "jobs.json"));
        var saved = await store.LoadAsync();
        var source = Path.Combine(Path.GetDirectoryName(DesktopPaths.DataDirectory)!, "manual-reopen-base");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "1Cv8.1CD"), "isolated smoke database");
        var job = new BackupJob { Name = "Ручной запуск после открытия окна", Source = source, Enabled = false,
            OneCMethod = OneCBackupMethod.FileArchive, ScheduleKind = ScheduleKind.Manual,
            DestinationDirectory = Path.Combine(source, "copies") };
        await store.SaveAsync([..saved, job]);
        using var shutdown = new CancellationTokenSource();
        MainWindow? reopened = null;
        var host = Task.Run(() => new LocalAgentHost(DesktopPaths.DataDirectory, new WaitingManualExecutor(), new WindowsFtpCredentialStore())
            .RunAsync(cancellationToken: shutdown.Token));
        try
        {
            await WaitUntil(() => window.SmokeBackgroundManualReady, "Агент не готов к проверке повторного открытия окна.");
            var state = (await new LocalAgentControlStore(DesktopPaths.DataDirectory).ReadAsync())!;
            var queue = new LocalManualRunQueue(DesktopPaths.DataDirectory);
            var request = await queue.EnqueueAsync(state.SessionId, job.Id);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while ((await queue.ReadReceiptAsync(request.RequestId, timeout.Token))?.Phase != LocalManualRunPhase.Accepted)
                await Task.Delay(25, timeout.Token);
            while ((await queue.PendingAsync(timeout.Token)).Any(r => r.RequestId == request.RequestId))
                await Task.Delay(25, timeout.Token);
            reopened = new MainWindow { SmokeMode = true, Width = 900, Height = 580 };
            reopened.Show();
            await WaitUntil(() => reopened.SmokeReady && reopened.SmokeBackgroundManualReady, "Повторно открытое окно не загрузило состояние агента.");
            var grid = (DataGrid)reopened.FindName("Jobs");
            grid.SelectedItem = grid.Items.OfType<BackupJob>().Single(j => j.Id == job.Id);
            var runButton = (Button)reopened.FindName("RunButton");
            if (runButton.IsEnabled || !runButton.ToolTip.ToString()!.Contains("уже принят"))
                throw new InvalidOperationException("Повторно открытое окно допускает дублирование принятого ручного запуска.");
            screenshot("manual-queue-reopened-window", reopened);
            grid.SelectedItem = grid.Items.OfType<BackupJob>().First(j => j.Id != job.Id);
            if (!runButton.IsEnabled) throw new InvalidOperationException("Принятая команда блокирует другие сохранённые задания.");
        }
        finally
        {
            reopened?.Close();
            shutdown.Cancel();
            await host.WaitAsync(TimeSpan.FromSeconds(10));
            await store.SaveAsync(saved);
        }
    }
}
