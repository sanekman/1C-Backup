using System.IO;
using System.Windows;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private bool checkingSource;
    private bool BackgroundAcceptsSourceChecks => backgroundBusy && !backgroundStarting && BackgroundExecutionMatchesMode
        && LocalSourceCheckQueue.IsAvailable(backgroundState, DateTimeOffset.UtcNow);
    internal bool SmokeBackgroundSourceReady => BackgroundAcceptsSourceChecks;
    private async void CheckSource_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckSourceButton.IsEnabled || Jobs.SelectedItem is not BackupJob job) return;
        var throughAgent = BackgroundAcceptsSourceChecks;
        using var lease = throughAgent ? null : AcquireEngineOrReport();
        if (!throughAgent && lease is null) return;
        using var cancellation = new CancellationTokenSource();
        copyVerification = cancellation;
        checkingSource = checkingStorages = true;
        BeginReadinessReport(job, ReadinessKind.Source, throughAgent);
        StorageCheckResults.Visibility = Visibility.Visible;
        StorageCheckResults.IsExpanded = false;
        StorageCheckResults.Header = "Проверяется источник: " + job.Name;
        StorageCheckDetails.Text = throughAgent
            ? "Запрос передаётся работающему агенту. Источник проверяется от его пользователя; расписание продолжит работать. Копия не создаётся."
            : "Проверка выполняется из окна программы, от его пользователя. Копия не создаётся, платформа 1С не запускается.";
        Status.Text = StorageCheckResults.Header.ToString();
        UpdateRunControls();
        try
        {
            SourceCheckReport report;
            if (throughAgent)
            {
                var queue = new LocalSourceCheckQueue(DesktopPaths.DataDirectory);
                var request = await queue.EnqueueAsync(backgroundState!.SessionId, job, cancellation.Token);
                report = await queue.WaitAsync(request, cancellation.Token);
            }
            else
            {
                var checker = new SourceReadinessChecker(new BackupProcessRunner(), Path.Combine(DesktopPaths.DataDirectory, "work"));
                report = await Task.Run(() => checker.CheckAsync(job, cancellation.Token));
            }
            readinessOutcome = report.Cancelled ? ReadinessOutcome.Cancelled : report.Succeeded ? ReadinessOutcome.Success : ReadinessOutcome.Failed;
            StorageCheckResults.Header = report.Cancelled ? "Проверка источника отменена: " + job.Name
                : report.Succeeded ? "Источник доступен: " + job.Name : "Источник не прошёл проверку: " + job.Name;
            StorageCheckDetails.Text = "Задание: " + job.Name + "\nИсточник: " + job.Source
                + (throughAgent ? "\nПроверка через агента. Пользователь исполнителя: " : "\nПроверка из окна программы. Пользователь: ") + (report.ExecutionIdentity?.Account ?? "не определён")
                + "\n" + ReadinessTime(report.StartedUtc, report.FinishedUtc)
                + "\n\n" + report.Details
                + (throughAgent ? "\n\nДоступ проверен от указанного пользователя агента в момент проверки. Это не подтверждает создание и восстановление копии."
                    : "\n\nДоступ службы Windows этим результатом не подтверждён. Первую копию выполните через того исполнителя, который будет работать автоматически.")
                + "\n\nРезультат на " + DateTimeOffset.Now.ToString("dd.MM.yyyy HH:mm:ss") + ". Копии, история и правила хранения не изменялись.";
            StorageCheckResults.IsExpanded = !report.Succeeded;
            Status.Text = StorageCheckResults.Header.ToString();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            readinessOutcome = ReadinessOutcome.Cancelled;
            StorageCheckResults.IsExpanded = true;
            StorageCheckResults.Header = Status.Text = "Проверка источника отменена: " + job.Name;
            StorageCheckDetails.Text = ReadinessFailureDetails(job, "Проверка отменена. Часть источника могла быть проверена; доступ ко всему источнику не подтверждён.");
        }
        catch (Exception ex)
        { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex);
            StorageCheckResults.Header = Status.Text = "Проверка источника не выполнена: " + job.Name;
            readinessOutcome = ReadinessOutcome.Failed;
            StorageCheckDetails.Text = ReadinessFailureDetails(job, ex.Message);
            StorageCheckResults.IsExpanded = true;
        }
        finally
        {
            checkingSource = checkingStorages = false; copyVerification = null;
            StorageCheckResults.Visibility = Jobs.SelectedItem is BackupJob selected && selected.Id == job.Id
                ? Visibility.Visible : Visibility.Collapsed;
            UpdateRunControls();
        }
    }
}
