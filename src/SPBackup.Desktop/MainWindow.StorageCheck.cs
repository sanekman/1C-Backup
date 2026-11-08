using System.IO;
using System.Windows;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private static string ReadinessTime(DateTimeOffset? started, DateTimeOffset? finished)
    {
        if (started is null || finished is null) return "Время проверки не получено от исполнителя.";
        return "Начало: " + started.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss")
            + " · окончание: " + finished.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss")
            + " · длительность: " + Math.Max(0, (finished.Value - started.Value).TotalSeconds).ToString("0.0") + " с";
    }

    private bool BackgroundAcceptsStorageChecks => backgroundBusy && !backgroundStarting
        && LocalStorageCheckQueue.IsAvailable(backgroundState, DateTimeOffset.UtcNow);
    internal bool SmokeBackgroundStorageReady => BackgroundAcceptsStorageChecks;

    private void StorageCheckResults_Expanded(object sender, RoutedEventArgs e)
    {
        // The actions panel has its own scrollbar; wait for the new report height before revealing it.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (StorageCheckResults.IsExpanded)
            {
                StorageCheckResults.UpdateLayout();
                StorageCheckResults.BringIntoView();
            }
        }));
    }

    private void JobsView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (StorageCheckDetails is not null) StorageCheckDetails.MaxHeight = ActualHeight < 650 ? 60 : 130;
        // Keep room for the table header and at least two job rows in the
        // compact window, even when delivery progress has many destinations.
        if (JobActionsContainer is not null)
            JobActionsContainer.MaxHeight = Math.Max(120, Math.Min(JobsView.ActualHeight * 0.55, JobsView.ActualHeight - 180));
    }

    private async void CheckStorage_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckStorageButton.IsEnabled || Jobs.SelectedItem is not BackupJob job) return;
        var throughAgent = BackgroundAcceptsStorageChecks;
        using var lease = throughAgent ? null : AcquireEngineOrReport();
        if (!throughAgent && lease is null) return;
        using var cancellation = new CancellationTokenSource();
        copyVerification = cancellation;
        checkingStorages = true;
        BeginReadinessReport(job, ReadinessKind.Storages, throughAgent);
        StorageCheckResults.Visibility = Visibility.Visible;
        StorageCheckResults.IsExpanded = false;
        StorageCheckResults.Header = "Проверяются хранилища: " + job.Name;
        StorageCheckDetails.Text = throughAgent ? "Запрос передаётся работающему агенту. Будут проверены временная папка и доступ к хранилищам от его пользователя."
            : "Проверка выполняется из окна программы: временная папка и запись, чтение и удаление тестового файла.";
        Status.Text = "Проверяются хранилища: " + job.Name;
        UpdateRunControls();
        try
        {
            StorageCheckReport report;
            if (throughAgent)
            {
                var queue = new LocalStorageCheckQueue(DesktopPaths.DataDirectory);
                var request = await queue.EnqueueAsync(backgroundState!.SessionId, job, cancellation.Token);
                report = await queue.WaitAsync(request, cancellation.Token);
            }
            else
            {
                var checker = new StorageReadinessChecker(backupStorage, Path.Combine(DesktopPaths.DataDirectory, "work"));
                report = await Task.Run(() => checker.CheckAsync(job, cancellation.Token));
            }
            var passed = report.Destinations.Count(result => result.Succeeded);
            var all = report.TemporaryError is null && report.Destinations.Count == job.StorageJobs.Count
                && passed == job.StorageJobs.Count && !report.Cancelled;
            readinessOutcome = report.Cancelled ? ReadinessOutcome.Cancelled : all ? ReadinessOutcome.Success : ReadinessOutcome.Failed;
            StorageCheckResults.Header = report.Cancelled ? "Проверка отменена: " + job.Name
                : all ? "Все хранилища доступны (" + (throughAgent ? "агент" : "окно") + "): " + job.Name
                    : $"Проверено хранилищ: {passed} из {job.StorageJobs.Count} — {job.Name}";
            var details = new List<string> { "Задание: " + job.Name,
                (throughAgent ? "Проверка через работающего агента." : "Проверка из окна программы.")
                    + " Пользователь: " + (report.ExecutionIdentity?.Account ?? "не определён") };
            details.Add(ReadinessTime(report.StartedUtc, report.FinishedUtc));
            if (report.Cancelled) details.Add("Проверка отменена. Успешные результаты относятся только к перечисленным хранилищам; остальные не подтверждены.");
            if (!throughAgent) details.Add("Доступ службы Windows может отличаться. Для проверки её доступа запустите службу и нажмите эту кнопку ещё раз.");
            if (report.TemporaryError is not null)
                details.Add("Ошибка временной папки: " + report.TemporaryDirectory + "\n" + report.TemporaryError);
            details.AddRange(report.Destinations.Where(result => !result.Succeeded)
                .Select(result => result.Destination + "\nЭтап: " + result.FailureStageDescription
                    + "\nОшибка: " + result.Error
                    + (result.CleanupFailed ? "\nПроверьте указанный тестовый файл: автоматическая очистка не завершилась. Не удаляйте резервные копии ради повторной проверки." : "")));
            if (report.TemporaryError is null)
                details.Add("Временная папка: " + report.TemporaryDirectory + "\nЗапись и чтение временного файла проверены.\nСвободное место: " + report.TemporaryAvailableSpaceDescription);
            details.AddRange(report.Destinations.Where(result => result.Succeeded)
                .Select(result => result.Destination + "\nЗапись, чтение и удаление проверены.\nСвободное место: " + result.AvailableSpaceDescription));
            if (report.Destinations.Count == 0) details.Add("Хранилища не проверены.");
            details.Add("Это проверка доступа. Свободное место может измениться; размер будущего архива заранее неизвестен. Создание копии базы проверяется отдельным запуском.");
            StorageCheckDetails.Text = string.Join("\n\n", details);
            StorageCheckResults.IsExpanded = !all;
            Status.Text = StorageCheckResults.Header.ToString();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            readinessOutcome = ReadinessOutcome.Cancelled;
            StorageCheckResults.IsExpanded = true;
            StorageCheckResults.Header = Status.Text = (throughAgent ? "Ожидание проверки отменено: " : "Проверка отменена: ") + job.Name;
            StorageCheckDetails.Text = ReadinessFailureDetails(job, throughAgent ? "Ожидание отменено. Исполнитель может ещё убирать свои тестовые файлы. Проверьте его состояние перед повтором; расписание продолжает работать."
                : "Проверка отменена. Часть операций могла быть выполнена; полный доступ к хранилищам не подтверждён.");
        }
        catch (Exception ex)
        { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex);
            StorageCheckResults.Header = Status.Text = "Проверка не выполнена: " + job.Name;
            readinessOutcome = ReadinessOutcome.Failed;
            StorageCheckDetails.Text = ReadinessFailureDetails(job, ex.Message);
            StorageCheckResults.IsExpanded = true;
        }
        finally
        {
            checkingStorages = false; copyVerification = null;
            StorageCheckResults.Visibility = Jobs.SelectedItem is BackupJob selected && selected.Id == job.Id
                ? Visibility.Visible : Visibility.Collapsed;
            UpdateRunControls();
        }
    }
}
