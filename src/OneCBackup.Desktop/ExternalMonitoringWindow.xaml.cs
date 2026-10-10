using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

public partial class ExternalMonitoringWindow : Window
{
    internal bool SmokeReady => ready && !busy;
    private readonly ExternalBackupMonitorStore store;
    private readonly ExternalBackupOutbox outbox;
    private readonly ExternalBackupPollingStatusStore pollingStatus;
    private readonly BackupEngineCoordinator engine;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(10) };
    private bool refreshing;
    private readonly AgentSettings settings;
    private List<ExternalBackupMonitor> monitors = [];
    private Guid? editingId;
    private bool ready;
    private readonly CancellationTokenSource lifetime = new();
    private bool busy;
    private CancellationTokenSource? collectionCancellation;

    public ExternalMonitoringWindow(AgentSettings settings)
    {
        this.settings = settings;
        var directory = DesktopPaths.DataDirectory;
        store = new ExternalBackupMonitorStore(Path.Combine(directory, "external-monitors.json"));
        outbox = new ExternalBackupOutbox(Path.Combine(directory, "external-backups.json"));
        pollingStatus = new ExternalBackupPollingStatusStore(directory);
        engine = new BackupEngineCoordinator(directory);
        InitializeComponent();
        SourceKind.ItemsSource = new Dictionary<ExternalBackupSourceKind, string>
        {
            [ExternalBackupSourceKind.SqlServerHistory] = "SQL Server: история msdb",
            [ExternalBackupSourceKind.PostgreSqlReports] = "PostgreSQL: отчёты скрипта"
        };
        SourceKind.SelectedValue = ExternalBackupSourceKind.SqlServerHistory;
        Zone.Text = TimeZoneInfo.Local.Id;
        Editor.IsEnabled = false;
        Loaded += async (_, _) =>
        {
            try
            {
                monitors = (await store.LoadAsync(lifetime.Token)).ToList();
                Sources.ItemsSource = monitors;
                await RefreshAsync();
                ready = true; Editor.IsEnabled = true;
                Status.Text = "Фоновый агент опрашивает включённые источники автоматически. Для ручной проверки остановите агент.";
                timer.Start();
            }
            catch (Exception ex) { Status.Text = "Не удалось прочитать настройки или историю: " + ex.Message; }
        };
        Closing += (_, e) =>
        {
            if (!busy) return;
            e.Cancel = true; lifetime.Cancel();
            Status.Text = "Останавливается проверка. Дождитесь завершения перед закрытием окна.";
        };
        timer.Tick += async (_, _) => await RefreshSafelyAsync();
        Closed += (_, _) => { timer.Stop(); lifetime.Dispose(); };
    }

    private async Task RefreshAsync()
    {
        Reports.ItemsSource = (await outbox.ReadAsync(lifetime.Token))
            .Where(r => r.AgentId == settings.AgentId).OrderByDescending(r => r.ObservedUtc).ToList();
        PollStatuses.ItemsSource = (await pollingStatus.ReadAsync(lifetime.Token)).OrderByDescending(p => p.PolledUtc).ToList();
    }

    private async Task RefreshSafelyAsync()
    {
        if (!ready || refreshing || lifetime.IsCancellationRequested) return;
        refreshing = true;
        try { await RefreshAsync(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Status.Text = "Ошибка чтения истории: " + ex.Message; }
        finally { refreshing = false; }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshSafelyAsync();

    private void Kind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SqlPanel is null || PostgresPanel is null) return;
        var sql = SourceKind.SelectedValue is ExternalBackupSourceKind.SqlServerHistory;
        SqlPanel.Visibility = sql ? Visibility.Visible : Visibility.Collapsed;
        PostgresPanel.Visibility = sql ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Source_Changed(object sender, SelectionChangedEventArgs e)
    {
        DeleteMonitorButton.IsEnabled = Sources.SelectedItem is ExternalBackupMonitor;
        if (Sources.SelectedItem is not ExternalBackupMonitor monitor) return;
        editingId = monitor.Id; MonitorName.Text = monitor.Name; SourceKind.SelectedValue = monitor.Kind;
        SqlServer.Text = monitor.SqlServer; Database.Text = monitor.DatabaseName; Zone.Text = monitor.TimeZoneId;
        Days.Text = monitor.LookbackDays.ToString(); ReportDirectory.Text = monitor.ReportDirectory;
        MonitorEnabled.IsChecked = monitor.Enabled; PollInterval.Text = monitor.PollIntervalSeconds.ToString();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        editingId = null; Sources.SelectedItem = null; MonitorName.Clear(); SqlServer.Clear(); Database.Clear();
        ReportDirectory.Clear(); Zone.Text = TimeZoneInfo.Local.Id; Days.Text = "30";
        MonitorEnabled.IsChecked = true; PollInterval.Text = "60";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!ready || busy) return;
        busy = true; Editor.IsEnabled = false;
        try
        {
            var monitor = new ExternalBackupMonitor
            {
                Id = editingId ?? Guid.NewGuid(), Name = MonitorName.Text.Trim(),
                Kind = (ExternalBackupSourceKind)SourceKind.SelectedValue,
                SqlServer = SqlServer.Text.Trim(), DatabaseName = Database.Text.Trim(), TimeZoneId = Zone.Text.Trim(),
                LookbackDays = int.TryParse(Days.Text, out var days) ? days : 0, ReportDirectory = ReportDirectory.Text.Trim(),
                Enabled = MonitorEnabled.IsChecked == true, PollIntervalSeconds = int.TryParse(PollInterval.Text, out var interval) ? interval : 0
            };
            monitor.Validate();
            var updated = monitors.Where(m => m.Id != monitor.Id).Append(monitor).ToList();
            await store.SaveAsync(updated, lifetime.Token);
            monitors = updated; Sources.ItemsSource = monitors; Sources.SelectedItem = monitor;
            Status.Text = "Источник сохранён. Проверка использует сохранённые настройки.";
        }
        catch (Exception ex) { Status.Text = "Не удалось сохранить: " + ex.Message; }
        finally { busy = false; Editor.IsEnabled = !lifetime.IsCancellationRequested; }
    }

    private async void Collect_Click(object sender, RoutedEventArgs e)
    {
        if (!ready || busy || Sources.SelectedItem is not ExternalBackupMonitor monitor) return;
        busy = true; Editor.IsEnabled = false; Status.Text = "Чтение сведений о внешних копиях…";
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        collectionCancellation = cancellation; CancelCollectButton.IsEnabled = true;
        try
        {
            using var lease = engine.Acquire();
            IExternalBackupSource source = monitor.Kind == ExternalBackupSourceKind.SqlServerHistory
                ? new SqlServerBackupHistorySource() : new PostgreSqlBackupReportSource();
            var observations = await Task.Run(() => source.ReadAsync(monitor, cancellation.Token));
            var added = await outbox.ObserveAsync(settings, monitor, observations, cancellation.Token);
            await RefreshAsync();
            Status.Text = $"Проверка завершена. Получено: {observations.Count}, новых: {added}. Это результат чтения истории, а не проверка восстановления.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { Status.Text = "Проверка отменена. Полученные ранее наблюдения сохранены."; }
        catch (Exception ex) { Status.Text = "Ошибка проверки. Прежняя история сохранена: " + ex.Message; }
        finally
        {
            collectionCancellation = null; CancelCollectButton.IsEnabled = false;
            busy = false; Editor.IsEnabled = !lifetime.IsCancellationRequested;
        }
    }

    private void CancelCollect_Click(object sender, RoutedEventArgs e)
    {
        collectionCancellation?.Cancel(); CancelCollectButton.IsEnabled = false;
        Status.Text = "Отменяется ручная проверка…";
    }

    private void BrowseReports_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Каталог отчётов PostgreSQL" };
        if (Directory.Exists(ReportDirectory.Text)) dialog.InitialDirectory = ReportDirectory.Text;
        if (dialog.ShowDialog(this) == true) ReportDirectory.Text = dialog.FolderName;
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!ready || busy || Sources.SelectedItem is not ExternalBackupMonitor monitor) return;
        if (MessageBox.Show(this, $"Удалить источник «{monitor.Name}»? Сохранённые наблюдения и внешние файлы резервных копий останутся.",
            "Удаление источника", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        busy = true; Editor.IsEnabled = false;
        try
        {
            var updated = monitors.Where(source => source.Id != monitor.Id).ToList();
            await store.SaveAsync(updated, lifetime.Token);
            monitors = updated; Sources.ItemsSource = monitors;
            New_Click(sender, e);
            Status.Text = "Источник удалён. Уже начавшийся фоновый опрос может завершиться; новые опросы этого источника прекращаются.";
        }
        catch (Exception ex) { Status.Text = "Не удалось удалить источник: " + ex.Message; }
        finally { busy = false; Editor.IsEnabled = !lifetime.IsCancellationRequested; }
    }

    private void Report_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Reports.SelectedItem is not ExternalBackupReport report)
        { ReportDetails.Text = "Выберите наблюдение в таблице."; return; }
        var observation = report.Observation;
        ReportDetails.Text = $"База: {observation.DatabaseName}\nЭкземпляр: {observation.Instance}\nРезультат: {observation.OutcomeDescription}\n"
            + $"Тип копии: {observation.BackupType}\nНачало: {observation.StartedAt:dd.MM.yyyy HH:mm:ss}\n"
            + $"Окончание: {observation.FinishedAt:dd.MM.yyyy HH:mm:ss}\nЧасовой пояс источника: {observation.TimeZoneId}\n"
            + $"Размер: {observation.SizeBytes?.ToString() ?? "не указан"} байт\nПути: {observation.LocationDescription}\n"
            + $"Основание результата: {observation.EvidenceDescription}\nОтправка: {report.DeliveryDescription}\n"
            + $"ID наблюдения: {report.ReportId}\n" + (observation.Error is null ? "" : "Ошибка: " + observation.Error);
    }
}
