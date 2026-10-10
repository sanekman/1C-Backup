using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

public partial class MainWindow : Window
{
    internal bool SmokeReady => ready && activeRun is null && !manualPending && copyVerification is null;
    private readonly JobStore store = new(Path.Combine(DesktopPaths.DataDirectory, "jobs.json"));
    private List<BackupJob> jobs = [];
    private readonly AgentSettingsStore agentSettingsStore = new(Path.Combine(DesktopPaths.DataDirectory, "agent.json"));
    private readonly AgentConfigurationCache agentCache = new(Path.Combine(DesktopPaths.DataDirectory, "server-jobs.json"));
    private AgentSettings? agentSettings;
    private Guid? editingId;
    private bool canSave;
    private readonly BackupRunHistory history = new(Path.Combine(DesktopPaths.DataDirectory, "runs.json"));
    private readonly LocalBackupRunner runner;
    private readonly BackupCatalog catalog;
    private readonly RoutedBackupStorage backupStorage;
    private readonly BackupEngineCoordinator engine;
    private readonly LocalAgentControlStore backgroundControl;
    private readonly DispatcherTimer backgroundTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private LocalAgentState? backgroundState;
    private bool backgroundBusy, backgroundStarting, refreshingBackground;
    private Process? launchedAgent;
    private DateTimeOffset lastBackgroundRefresh;
    private CancellationTokenSource? copyVerification;
    private bool copiesRefreshing;
    private int copiesRefreshVersion;
    private CancellationTokenSource? activeRun;
    private readonly LocalBackupDispatcher dispatcher;
    private CancellationTokenSource? schedulerCancellation;
    private Task? schedulingTask;
    private bool schedulerStopping;
    private bool manualPending;
    private bool ready;
    private readonly WindowsFtpCredentialStore ftpCredentials = new();

    public MainWindow()
    {
        InitializeComponent();
        var dataDirectory = DesktopPaths.DataDirectory;
        engine = new BackupEngineCoordinator(dataDirectory);
        backgroundControl = new LocalAgentControlStore(dataDirectory);
        backupStorage = new RoutedBackupStorage(ftpCredentials, Path.Combine(dataDirectory, "ftp-verification"));
        catalog = new BackupCatalog(Path.Combine(dataDirectory, "copies.json"));
        runner = new LocalBackupRunner(new DatabaseBackupExecutor(new BackupProcessRunner()),
            new BackupCompletion(backupStorage, catalog),
            history, Path.Combine(dataDirectory, "work"));
        dispatcher = new LocalBackupDispatcher(store, (job, token) =>
            Dispatcher.InvokeAsync(() => ExecuteJobAsync(job, token)).Task.Unwrap());
        Closing += (_, e) =>
        {
            if (activeRun is null && schedulingTask is null && !manualPending && copyVerification is null) return;
            e.Cancel = true;
            Status.Text = "Остановите планировщик и дождитесь завершения операции перед закрытием программы.";
        };
        AgentModePicker.ItemsSource = new Dictionary<AgentMode, string>
        {
            [AgentMode.Standalone] = "Автономный агент",
            [AgentMode.Managed] = "Управление через сервер"
        };
        AgentModePicker.SelectedValue = AgentMode.Standalone;
        Kind.ItemsSource = Enum.GetValues<DatabaseKind>();
        Kind.SelectedItem = DatabaseKind.OneCFile;
        OneCMethodPicker.ItemsSource = new Dictionary<OneCBackupMethod, string>
        {
            [OneCBackupMethod.PlatformDump] = "Выгрузка .dt через платформу 1С",
            [OneCBackupMethod.FileArchive] = "Архив файла базы без платформы"
        };
        OneCMethodPicker.SelectedValue = OneCBackupMethod.PlatformDump;
        Storage.ItemsSource = new Dictionary<StorageKind, string>
        {
            [StorageKind.LocalDirectory] = "Локальный диск",
            [StorageKind.NetworkShare] = "Сетевая папка (UNC)",
            [StorageKind.Ftp] = "FTP / FTPS"
        };
        Storage.SelectedValue = StorageKind.LocalDirectory;
        Schedule.ItemsSource = new Dictionary<ScheduleKind, string>
        {
            [ScheduleKind.Manual] = "Только вручную",
            [ScheduleKind.Daily] = "Ежедневно",
            [ScheduleKind.Weekly] = "По выбранным дням недели"
        };
        Schedule.SelectedValue = ScheduleKind.Daily;
        Mon.IsChecked = true;
        Zone.Text = TimeZoneInfo.Local.Id;
        SaveButton.IsEnabled = DeleteButton.IsEnabled = JobEditor.IsEnabled = false;
        Loaded += async (_, _) =>
        {
            try
            {
                agentSettings = await agentSettingsStore.LoadAsync();
                AgentIdentity.Text = "Агент: " + agentSettings.AgentId;
                AgentModePicker.SelectedValue = agentSettings.Mode;
                ServerAddress.Text = agentSettings.ServerAddress;
                AgentCredentialKey.Text = agentSettings.CredentialKey;
                SyncInterval.Text = agentSettings.SyncIntervalSeconds.ToString();
                await LoadEffectiveJobsAsync();
                try { using var lease = engine.Acquire(); await history.RecoverInterruptedAsync(); }
                catch (BackupEngineBusyException) { /* The background owner recovers its own history. */ }
                await RefreshHistoryAsync();
                SaveAgentSettingsButton.IsEnabled = true;
                ExternalMonitoringButton.IsEnabled = true;
                ready = true;
                try { AgentStartup.IsChecked = CurrentUserAgentStartup.IsEnabled(); }
                catch (Exception ex) { StartupStatus.Text = "Не удалось прочитать автозапуск: " + ex.Message; }
                await RefreshBackgroundStateAsync();
                backgroundTimer.Start();
                UpdateRunControls();
            }
            catch (Exception ex)
            {
                canSave = false;
                JobEditor.IsEnabled = SaveButton.IsEnabled = DeleteButton.IsEnabled = RunButton.IsEnabled = false;
                Status.Text = "Не удалось прочитать настройки. Сохранение отключено, чтобы не затереть файл: " + ex.Message;
            }
        };
        backgroundTimer.Tick += async (_, _) => await RefreshBackgroundStateAsync();
        Closed += (_, _) => { backgroundTimer.Stop(); launchedAgent?.Dispose(); };
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!canSave) return;
        var scheduleKind = (ScheduleKind)Schedule.SelectedValue;
        var at = new TimeOnly(23, 0);
        if ((scheduleKind != ScheduleKind.Manual && !TimeOnly.TryParseExact(DailyAt.Text.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out at))
            || !int.TryParse(KeepLast.Text, out var keep))
        { Status.Text = "Укажите время в формате чч:мм и целое число копий."; return; }
        var storageKind = (StorageKind)Storage.SelectedValue;
        OneCSettings? oneC = null;
        var oneCMethod = OneCMethodPicker.SelectedValue is OneCBackupMethod method ? method : OneCBackupMethod.PlatformDump;
        if (Kind.SelectedItem is DatabaseKind.OneCFile && oneCMethod == OneCBackupMethod.PlatformDump && !string.IsNullOrWhiteSpace(OneCExecutable.Text))
        {
            if (!int.TryParse(OneCTimeout.Text, out var timeout))
            { Status.Text = "Укажите целое число минут ожидания выгрузки."; return; }
            oneC = new OneCSettings { ExecutablePath = OneCExecutable.Text.Trim(), TimeoutMinutes = timeout };
        }
        var ftpPort = 21;
        SqlServerBackupSettings? sql = null;
        PostgreSqlBackupSettings? postgres = null;
        if (Kind.SelectedItem is DatabaseKind.SqlServer)
        {
            if (!int.TryParse(SqlBackupTimeout.Text, out var timeout))
            { Status.Text = "Укажите целое число минут ожидания SQL-копии."; return; }
            sql = new SqlServerBackupSettings { Server = SqlBackupServer.Text.Trim(),
                ServerBackupDirectory = SqlServerDirectory.Text.Trim(), AgentReadDirectory = SqlAgentDirectory.Text.Trim(), TimeoutMinutes = timeout };
        }
        if (Kind.SelectedItem is DatabaseKind.PostgreSql)
        {
            if (!int.TryParse(PgPort.Text, out var port) || !int.TryParse(PgTimeout.Text, out var timeout))
            { Status.Text = "Укажите целые числа для порта и времени ожидания PostgreSQL."; return; }
            postgres = new PostgreSqlBackupSettings { Host = PgHost.Text.Trim(), Port = port, Username = PgUsername.Text.Trim(),
                PgDumpPath = PgDumpPath.Text.Trim(), PgRestorePath = PgRestorePath.Text.Trim(), TimeoutMinutes = timeout };
        }
        if (storageKind == StorageKind.Ftp && !int.TryParse(FtpPort.Text, out ftpPort))
        { Status.Text = "Укажите целое число в поле порта FTP."; return; }
        var job = new BackupJob
        {
            Id = editingId ?? Guid.NewGuid(), Name = JobName.Text.Trim(), Kind = (DatabaseKind)Kind.SelectedItem,
            Source = Source.Text.Trim(), DestinationDirectory = Destination.Text.Trim(),
            DailyAt = at, TimeZoneId = Zone.Text.Trim(), KeepLast = keep, Enabled = Enabled.IsChecked == true,
            ArchiveCopies = ArchiveCopies.IsChecked == true, StorageKind = storageKind,
            ScheduleKind = scheduleKind, DaysOfWeek = SelectedDays(),
            OneC = oneC,
            OneCMethod = oneCMethod,
            SqlServer = sql, PostgreSql = postgres,
            Ftp = storageKind == StorageKind.Ftp ? new FtpSettings
            {
                Host = FtpHost.Text.Trim(), Port = ftpPort, UseTls = FtpTls.IsChecked == true,
                CredentialKey = FtpCredentialKey.Text.Trim()
            } : null
        };
        var errors = job.Validate();
        if (errors.Count > 0) { Status.Text = string.Join(" ", errors); return; }
        var updated = jobs.Where(j => j.Id != job.Id).Append(job).ToList();
        if (await PersistAsync(updated)) { JobSearch.Clear(); Jobs.SelectedItem = job; Status.Text = "Настройки сохранены. Кнопка создания копии использует сохранённое задание."; }
    }

    private async Task<bool> PersistAsync(List<BackupJob> updated)
    {
        canSave = false;
        SaveButton.IsEnabled = DeleteButton.IsEnabled = false;
        SaveAgentSettingsButton.IsEnabled = false;
        try
        {
            await store.SaveAsync(updated);
            jobs = updated;
            Jobs.ItemsSource = jobs;
            ApplyJobFilter();
            await RefreshCopiesAsync();
            return true;
        }
        catch (Exception ex) { Status.Text = "Не удалось сохранить: " + ex.Message; return false; }
        finally
        {
            canSave = agentSettings?.Mode == AgentMode.Standalone;
            SaveButton.IsEnabled = DeleteButton.IsEnabled = JobEditor.IsEnabled = canSave;
            SaveAgentSettingsButton.IsEnabled = true;
            UpdateRunControls();
        }
    }

    private async Task LoadEffectiveJobsAsync()
    {
        canSave = false;
        JobEditor.IsEnabled = SaveButton.IsEnabled = DeleteButton.IsEnabled = false;
        jobs = []; Jobs.ItemsSource = jobs; editingId = null;
        var configuration = new AgentConfiguration(store, agentCache);
        jobs = (await configuration.GetEffectiveJobsAsync(agentSettings!)).ToList();
        Jobs.ItemsSource = jobs;
        ApplyJobFilter();
        canSave = agentSettings!.Mode == AgentMode.Standalone;
        JobEditor.IsEnabled = SaveButton.IsEnabled = DeleteButton.IsEnabled = canSave;
        Status.Text = canSave ? "Автономный режим: локальные задания. Пароли в файл настроек не записываются."
            : "Серверный режим: сохранённые серверные задания доступны только для просмотра. Автоматическая связь ещё не подключена.";
        UpdateRunControls();
        await RefreshCopiesAsync();
    }

    private void AgentMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ServerPanel is not null)
            ServerPanel.Visibility = AgentModePicker.SelectedValue is AgentMode.Managed ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SaveAgentSettings_Click(object sender, RoutedEventArgs e)
    {
        if (agentSettings is null || schedulingTask is not null || manualPending || activeRun is not null || copyVerification is not null) return;
        using var lease = AcquireEngineOrReport();
        if (lease is null) return;
        var mode = (AgentMode)AgentModePicker.SelectedValue;
        var interval = agentSettings.SyncIntervalSeconds;
        if (mode == AgentMode.Managed && !int.TryParse(SyncInterval.Text, out interval))
        { Status.Text = "Укажите целое число секунд в поле интервала связи."; return; }
        var updated = agentSettings with { Mode = mode, ServerAddress = ServerAddress.Text.Trim(),
            CredentialKey = AgentCredentialKey.Text.Trim(), SyncIntervalSeconds = interval };
        var errors = updated.Validate();
        if (errors.Count != 0) { Status.Text = string.Join(" ", errors); return; }
        SaveAgentSettingsButton.IsEnabled = false;
        canSave = false; JobEditor.IsEnabled = SaveButton.IsEnabled = DeleteButton.IsEnabled = false;
        try
        {
            await agentSettingsStore.SaveAsync(updated);
            agentSettings = updated;
            await LoadEffectiveJobsAsync();
        }
        catch (Exception ex) { Status.Text = "Не удалось применить настройки агента: " + ex.Message; }
        finally { UpdateRunControls(); }
    }

    private void Jobs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Jobs.SelectedItem is not BackupJob job) { UpdateRunControls(); return; }
        editingId = job.Id; Zone.Text = job.TimeZoneId;
        OneCExecutable.Text = job.OneC?.ExecutablePath ?? "";
        OneCTimeout.Text = (job.OneC?.TimeoutMinutes ?? 120).ToString();
        SqlBackupServer.Text = job.SqlServer?.Server ?? "";
        SqlServerDirectory.Text = job.SqlServer?.ServerBackupDirectory ?? "";
        SqlAgentDirectory.Text = job.SqlServer?.AgentReadDirectory ?? "";
        SqlBackupTimeout.Text = (job.SqlServer?.TimeoutMinutes ?? 120).ToString();
        PgHost.Text = job.PostgreSql?.Host ?? "localhost"; PgPort.Text = (job.PostgreSql?.Port ?? 5432).ToString();
        PgUsername.Text = job.PostgreSql?.Username ?? "postgres";
        PgDumpPath.Text = job.PostgreSql?.PgDumpPath ?? ""; PgRestorePath.Text = job.PostgreSql?.PgRestorePath ?? "";
        PgTimeout.Text = (job.PostgreSql?.TimeoutMinutes ?? 120).ToString();
        JobName.Text = job.Name; Kind.SelectedItem = job.Kind; Source.Text = job.Source;
        OneCMethodPicker.SelectedValue = job.OneCMethod;
        Destination.Text = job.DestinationDirectory; DailyAt.Text = job.DailyAt.ToString("HH:mm", CultureInfo.InvariantCulture);
        KeepLast.Text = job.KeepLast.ToString(); Enabled.IsChecked = job.Enabled;
        ArchiveCopies.IsChecked = job.ArchiveCopies;
        UpdateOneCControls();
        Storage.SelectedValue = job.StorageKind;
        FtpHost.Text = job.Ftp?.Host ?? ""; FtpPort.Text = (job.Ftp?.Port ?? 21).ToString();
        FtpTls.IsChecked = job.Ftp?.UseTls ?? true; FtpCredentialKey.Text = job.Ftp?.CredentialKey ?? "";
        Schedule.SelectedValue = job.ScheduleKind;
        SetDays(job.DaysOfWeek);
        UpdateSchedulePreview();
        UpdateRunControls();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        JobSearch.Clear();
        Jobs.SelectedItem = null; editingId = null; Zone.Text = TimeZoneInfo.Local.Id;
        JobName.Clear(); Source.Clear(); Destination.Clear(); Kind.SelectedItem = DatabaseKind.OneCFile;
        OneCMethodPicker.SelectedValue = OneCBackupMethod.PlatformDump;
        OneCExecutable.Clear(); OneCTimeout.Text = "120";
        SqlBackupServer.Clear(); SqlServerDirectory.Clear(); SqlAgentDirectory.Clear(); SqlBackupTimeout.Text = "120";
        PgHost.Text = "localhost"; PgPort.Text = "5432"; PgUsername.Text = "postgres";
        PgDumpPath.Clear(); PgRestorePath.Clear(); PgTimeout.Text = "120";
        DailyAt.Text = "23:00"; KeepLast.Text = "7"; Enabled.IsChecked = true;
        ArchiveCopies.IsChecked = false;
        Storage.SelectedValue = StorageKind.LocalDirectory;
        FtpHost.Clear(); FtpPort.Text = "21"; FtpTls.IsChecked = true; FtpCredentialKey.Clear();
        Schedule.SelectedValue = ScheduleKind.Daily; SetDays(WeekDays.Monday);
        UpdateSchedulePreview();
        UpdateOneCControls();
        UpdateRunControls();
    }

    private void Kind_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateOneCControls();
    }

    private void OneCMethod_Changed(object sender, SelectionChangedEventArgs e) => UpdateOneCControls();

    private void UpdateOneCControls()
    {
        if (OneCPanel is null || OneCMethodPanel is null || ArchiveCopies is null) return;
        var isOneC = Kind.SelectedItem is DatabaseKind.OneCFile;
        var fileArchive = isOneC && OneCMethodPicker.SelectedValue is OneCBackupMethod.FileArchive;
        OneCMethodPanel.Visibility = isOneC ? Visibility.Visible : Visibility.Collapsed;
        OneCPanel.Visibility = isOneC && !fileArchive ? Visibility.Visible : Visibility.Collapsed;
        SqlBackupPanel.Visibility = Kind.SelectedItem is DatabaseKind.SqlServer ? Visibility.Visible : Visibility.Collapsed;
        PostgresBackupPanel.Visibility = Kind.SelectedItem is DatabaseKind.PostgreSql ? Visibility.Visible : Visibility.Collapsed;
        FileArchiveHint.Visibility = fileArchive ? Visibility.Visible : Visibility.Collapsed;
        ArchiveCopies.IsEnabled = !fileArchive;
        if (fileArchive) ArchiveCopies.IsChecked = true;
        SourceBrowseButton.Visibility = isOneC ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateRunControls()
    {
        var idle = activeRun is null && !manualPending && !schedulerStopping && copyVerification is null;
        var engineAvailable = !backgroundBusy && !backgroundStarting;
        NewJobButton.IsEnabled = ready && canSave && idle;
        JobsSummary.Text = $"Всего: {jobs.Count} · Включено: {jobs.Count(job => job.Enabled)}";
        RunButton.IsEnabled = ready && canSave && idle && engineAvailable && Jobs.SelectedItem is BackupJob;
        JobEditor.IsEnabled = SaveButton.IsEnabled = DeleteButton.IsEnabled = ready && canSave && idle;
        AgentModePicker.IsEnabled = SaveAgentSettingsButton.IsEnabled = ready && idle && engineAvailable && schedulingTask is null;
        ExternalMonitoringButton.IsEnabled = ready && idle && schedulingTask is null;
        StartScheduleButton.IsEnabled = ready && canSave && idle && engineAvailable && schedulingTask is null;
        FtpCredentialsButton.IsEnabled = ready && canSave && idle && engineAvailable && schedulingTask is null;
        StopScheduleButton.IsEnabled = schedulingTask is not null && !schedulerStopping;
        RefreshCopiesButton.IsEnabled = ready && !copiesRefreshing;
        VerifyCopyButton.IsEnabled = ready && idle && engineAvailable && schedulingTask is null &&
            Copies.SelectedItem is BackupCopyRow selected && jobs.Any(job => job.Id == selected.Record.JobId);
        CancelCopyVerificationButton.IsEnabled = copyVerification is { IsCancellationRequested: false };
        OpenCopyDirectoryButton.IsEnabled = ready && Copies.SelectedItem is BackupCopyRow { CanOpenDirectory: true };
        StartBackgroundButton.IsEnabled = ready && canSave && idle && engineAvailable && schedulingTask is null;
        StopBackgroundButton.IsEnabled = ready && backgroundBusy && backgroundState?.Phase == LocalAgentPhase.Running;
        SaveStartupButton.IsEnabled = ready;
    }

    private IDisposable? AcquireEngineOrReport()
    {
        try { return engine.Acquire(); }
        catch (Exception ex) { Status.Text = "Исполнение недоступно: " + ex.Message; return null; }
    }

    private async Task RefreshBackgroundStateAsync()
    {
        if (refreshingBackground) return;
        refreshingBackground = true;
        try
        {
            backgroundBusy = engine.IsHeldElsewhere();
            backgroundState = await backgroundControl.ReadAsync();
            if (launchedAgent is not null && launchedAgent.HasExited)
            {
                var code = launchedAgent.ExitCode;
                launchedAgent.Dispose(); launchedAgent = null; backgroundStarting = false;
                if (code != 0) Status.Text = $"Фоновый агент завершился с кодом {code}. Проверьте режим и настройки заданий.";
            }
            if (backgroundBusy) backgroundStarting = false;
            var live = backgroundBusy && backgroundState is not null &&
                DateTimeOffset.UtcNow - backgroundState.UpdatedUtc < TimeSpan.FromSeconds(10) &&
                backgroundState.Phase is LocalAgentPhase.Running or LocalAgentPhase.Stopping;
            BackgroundStatus.Text = backgroundStarting ? "Агент запускается…" : live
                ? backgroundState!.Phase == LocalAgentPhase.Stopping ? "Останавливается текущая операция…"
                    : "Работает независимо от окна. " + (backgroundState.Error ?? backgroundState.MonitoringError ?? "Можно закрыть приложение.")
                : backgroundBusy ? "Рабочий каталог занят другим исполнителем."
                : backgroundState?.Phase == LocalAgentPhase.Faulted ? "Агент остановлен с ошибкой: " + backgroundState.Error
                : "Выключен. Запустите, чтобы выполнять расписание после закрытия окна.";
            if (backgroundBusy && DateTimeOffset.UtcNow - lastBackgroundRefresh > TimeSpan.FromSeconds(10))
            {
                lastBackgroundRefresh = DateTimeOffset.UtcNow;
                await RefreshHistoryAsync(); await RefreshCopiesAsync();
            }
        }
        catch (Exception ex) { BackgroundStatus.Text = "Не удалось проверить состояние агента: " + ex.Message; }
        finally { refreshingBackground = false; UpdateRunControls(); }
    }

    private async void StartBackground_Click(object sender, RoutedEventArgs e)
    {
        if (!StartBackgroundButton.IsEnabled) return;
        var executable = Path.Combine(AppContext.BaseDirectory, "OneCBackup.Agent.exe");
        if (!File.Exists(executable)) { Status.Text = "Фоновый агент не найден рядом с приложением. Используйте полный дистрибутив Windows."; return; }
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--run"); start.ArgumentList.Add("--quiet");
            start.ArgumentList.Add("--data-dir");
            start.ArgumentList.Add(DesktopPaths.DataDirectory);
            backgroundStarting = true; UpdateRunControls();
            launchedAgent = Process.Start(start) ?? throw new InvalidOperationException("Не удалось создать процесс агента.");
            await RefreshBackgroundStateAsync();
        }
        catch (Exception ex) { backgroundStarting = false; Status.Text = "Не удалось запустить агент: " + ex.Message; UpdateRunControls(); }
    }

    private async void StopBackground_Click(object sender, RoutedEventArgs e)
    {
        if (!StopBackgroundButton.IsEnabled || backgroundState is null) return;
        try
        {
            await backgroundControl.RequestStopAsync(backgroundState.SessionId);
            BackgroundStatus.Text = "Запрошена остановка агента. Дождитесь завершения операции.";
            StopBackgroundButton.IsEnabled = false;
        }
        catch (Exception ex) { Status.Text = "Не удалось остановить агент: " + ex.Message; }
    }

    private void SaveStartup_Click(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        try
        {
            CurrentUserAgentStartup.Save(AgentStartup.IsChecked == true);
            StartupStatus.Text = AgentStartup.IsChecked == true ? "Агент будет запускаться при входе текущего пользователя в Windows. После переноса папки программы сохраните настройку заново."
                : "Автозапуск выключен. Уже работающий агент можно остановить кнопкой «Остановить агент».";
        }
        catch (Exception ex) { StartupStatus.Text = "Не удалось изменить автозапуск: " + ex.Message; }
    }

    private void JobsNavigation_Click(object sender, RoutedEventArgs e) => ShowPage("jobs");

    private void JobSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Jobs is not null && JobsEmptyTitle is not null) ApplyJobFilter();
    }

    private void ApplyJobFilter()
    {
        var query = JobSearch.Text.Trim();
        var view = CollectionViewSource.GetDefaultView(Jobs.ItemsSource);
        if (view is not null)
        {
            view.Filter = item => item is BackupJob job && (query.Length == 0 ||
                job.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                job.Source.Contains(query, StringComparison.CurrentCultureIgnoreCase));
            view.Refresh();
        }
        JobsEmptyTitle.Text = query.Length == 0 ? "Добавьте первое задание" : "Задания не найдены";
        JobsEmptyHint.Text = query.Length == 0 ? "Укажите базу, хранилище и расписание.\nСохранённое задание появится здесь." : "Измените или очистите строку поиска.";
        UpdateRunControls();
    }

    private void BrowsePath_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string target }) return;
        var field = target switch
        {
            "Source" => Source, "Destination" => Destination, "OneCExecutable" => OneCExecutable,
            "SqlAgentDirectory" => SqlAgentDirectory, "PgDumpPath" => PgDumpPath, "PgRestorePath" => PgRestorePath,
            _ => null
        };
        if (field is null) return;
        try
        {
            var fileSource = target == "Source" && OneCMethodPicker.SelectedValue is OneCBackupMethod.FileArchive;
            if (fileSource || target is "OneCExecutable" or "PgDumpPath" or "PgRestorePath")
            {
                var dialog = new OpenFileDialog
                {
                    Title = fileSource ? "Выберите файл базы 1Cv8.1CD" : "Выберите исполняемый файл",
                    Filter = fileSource ? "Файл базы 1С (1Cv8.1CD)|1Cv8.1CD" : "Программы (*.exe)|*.exe",
                    CheckFileExists = true,
                    Multiselect = false
                };
                if (dialog.ShowDialog(this) == true) field.Text = dialog.FileName;
            }
            else
            {
                var dialog = new OpenFolderDialog { Title = "Выберите каталог", Multiselect = false };
                if (dialog.ShowDialog(this) == true) field.Text = dialog.FolderName;
            }
        }
        catch (Exception ex) { Status.Text = "Не удалось выбрать путь: " + ex.Message; }
    }
    private void HistoryNavigation_Click(object sender, RoutedEventArgs e) => ShowPage("history");
    private async void CopiesNavigation_Click(object sender, RoutedEventArgs e)
    {
        ShowPage("copies");
        if (ready) await RefreshCopiesAsync();
    }
    private void AgentNavigation_Click(object sender, RoutedEventArgs e) => ShowPage("agent");

    private void ShowPage(string page)
    {
        JobsView.Visibility = page == "jobs" ? Visibility.Visible : Visibility.Collapsed;
        HistoryView.Visibility = page == "history" ? Visibility.Visible : Visibility.Collapsed;
        CopiesView.Visibility = page == "copies" ? Visibility.Visible : Visibility.Collapsed;
        AgentView.Visibility = page == "agent" ? Visibility.Visible : Visibility.Collapsed;
        JobsNavigation.Tag = page == "jobs" ? "Active" : null;
        HistoryNavigation.Tag = page == "history" ? "Active" : null;
        CopiesNavigation.Tag = page == "copies" ? "Active" : null;
        AgentNavigation.Tag = page == "agent" ? "Active" : null;
        (PageTitle.Text, PageDescription.Text) = page switch
        {
            "history" => ("История запусков", "Результаты операций, созданные копии и ошибки. Время указано в UTC."),
            "copies" => ("Резервные копии", "Зарегистрированные копии всех заданий, расположение файлов и проверка целостности."),
            "agent" => ("Настройки агента", "Автономная работа и параметры будущего подключения к серверу управления."),
            _ => ("Задания резервного копирования", "Файловая 1С · Microsoft SQL Server · PostgreSQL")
        };
    }

    private async Task RefreshHistoryAsync()
    {
        RunHistory.ItemsSource = (await history.ReadAsync()).OrderByDescending(r => r.StartedUtc).ToList();
    }

    private async Task RefreshCopiesAsync()
    {
        var version = ++copiesRefreshVersion;
        copiesRefreshing = true;
        UpdateRunControls();
        var selectedId = (Copies.SelectedItem as BackupCopyRow)?.Record.Id;
        try
        {
            var records = await Task.Run(() => catalog.ReadAsync());
            if (version != copiesRefreshVersion) return;
            var rows = records.OrderByDescending(record => record.CompletedUtc).Select(record =>
                new BackupCopyRow(record, jobs.FirstOrDefault(job => job.Id == record.JobId)?.Name
                    ?? $"Задание недоступно ({record.JobId.ToString("N")[..8]})")).ToList();
            Copies.ItemsSource = rows;
            Copies.SelectedItem = rows.FirstOrDefault(row => row.Record.Id == selectedId);
            CopiesSummary.Text = rows.Count == 0 ? "Копий пока нет. Создайте первую копию в разделе «Задания»."
                : $"Зарегистрировано копий: {rows.Count}";
        }
        catch (Exception ex)
        {
            if (version != copiesRefreshVersion) return;
            Copies.ItemsSource = null;
            CopiesSummary.Text = "Не удалось прочитать журнал копий: " + ex.Message;
        }
        finally
        {
            if (version == copiesRefreshVersion) { copiesRefreshing = false; UpdateRunControls(); }
        }
    }

    private async void RefreshCopies_Click(object sender, RoutedEventArgs e)
    {
        if (ready && !copiesRefreshing) await RefreshCopiesAsync();
    }

    private void Copies_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRunControls();

    private async void VerifyCopy_Click(object sender, RoutedEventArgs e)
    {
        if (!ready || activeRun is not null || manualPending || schedulingTask is not null ||
            copyVerification is not null || Copies.SelectedItem is not BackupCopyRow row) return;
        var job = jobs.FirstOrDefault(job => job.Id == row.Record.JobId);
        if (job is null) { CopyVerificationStatus.Text = "Настройки задания недоступны. Проверить копию нельзя."; return; }
        using var lease = AcquireEngineOrReport();
        if (lease is null) { CopyVerificationStatus.Text = Status.Text; return; }
        using var cancellation = new CancellationTokenSource();
        copyVerification = cancellation;
        UpdateRunControls();
        CopyVerificationStatus.Text = "Проверяется размер и SHA256: " + row.Location;
        try
        {
            await Task.Run(() => new BackupCopyVerifier(backupStorage).VerifyAsync(row.Record, job, cancellation.Token));
            CopyVerificationStatus.Text = "Размер и SHA256 совпадают с журналом: " + row.Location
                + ". Проверка не заменяет пробное восстановление базы.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { CopyVerificationStatus.Text = "Проверка отменена: " + row.Location; }
        catch (Exception ex)
        { CopyVerificationStatus.Text = "Проверка не пройдена: " + row.Location + ". " + ex.Message; }
        finally { copyVerification = null; UpdateRunControls(); }
    }

    private void CancelCopyVerification_Click(object sender, RoutedEventArgs e)
    {
        copyVerification?.Cancel();
        UpdateRunControls();
    }

    private async void OpenCopyDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (Copies.SelectedItem is not BackupCopyRow { CanOpenDirectory: true } row) return;
        try
        {
            var directory = Path.GetDirectoryName(row.Location)!;
            if (!await Task.Run(() => Directory.Exists(directory)))
            { CopyVerificationStatus.Text = "Папка недоступна: " + directory; return; }
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add(directory);
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) { CopyVerificationStatus.Text = "Не удалось открыть папку: " + ex.Message; }
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (!canSave || activeRun is not null || copyVerification is not null || Jobs.SelectedItem is not BackupJob job) return;
        if (job.Kind == DatabaseKind.OneCFile && job.OneCMethod == OneCBackupMethod.PlatformDump && job.OneC is null)
        { Status.Text = "Укажите путь к 1cv8.exe и сохраните задание перед запуском."; return; }
        manualPending = true;
        UpdateRunControls();
        try { await dispatcher.ExecuteAsync(job); }
        catch (Exception ex) { Status.Text = "Не удалось запустить задание: " + ex.Message; }
        finally { manualPending = false; UpdateRunControls(); }
    }

    private async Task ExecuteJobAsync(BackupJob job, CancellationToken cancellationToken)
    {
        using var lease = engine.Acquire();
        activeRun = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        UpdateRunControls();
        CancelRunButton.IsEnabled = true;
        Status.Text = "Создаётся резервная копия: " + job.Name + ". Используются сохранённые настройки.";
        try
        {
            // File hashing/ZIP may do synchronous work before their first await.
            var result = await Task.Run(() => runner.RunAsync(job, activeRun.Token));
            Status.Text = result.StatusDescription + ": " + result.Details;
            if (result.Status != BackupRunStatus.Succeeded) Status.Text += " Рабочий каталог: " + result.WorkDirectory;
            await RefreshHistoryAsync();
            await RefreshCopiesAsync();
        }
        catch (Exception ex) { Status.Text = "Не удалось выполнить или записать результат: " + ex.Message; }
        finally
        {
            activeRun.Dispose(); activeRun = null;
            CancelRunButton.IsEnabled = false;
            UpdateRunControls();
        }
    }

    private async void StartSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (!ready || !canSave || schedulingTask is not null || activeRun is not null || copyVerification is not null || manualPending) return;
        using var scheduleLease = AcquireEngineOrReport();
        if (scheduleLease is null) return;
        // Reserve the state before the first await, so another click cannot start a second loop.
        schedulerCancellation = new CancellationTokenSource();
        schedulingTask = Task.CompletedTask;
        UpdateRunControls();
        try
        {
            await dispatcher.LoadScheduledJobsAsync(schedulerCancellation.Token);
            SchedulerStatus.Text = "Планировщик работает, пока программа открыта. Проверка расписания каждые 30 секунд.";
            schedulingTask = Task.Run(() => dispatcher.RunScheduleAsync(results =>
                Dispatcher.InvokeAsync(() =>
                {
                    var failures = results.Where(r => r.Error is not null).ToArray();
                    if (failures.Length != 0) Status.Text = "Ошибки запуска: " + string.Join("; ", failures.Select(r => r.Error!.Message));
                    UpdateSchedulePreview();
                }).Task, schedulerCancellation.Token));
            await schedulingTask;
        }
        catch (OperationCanceledException) when (schedulerCancellation.IsCancellationRequested)
        { SchedulerStatus.Text = "Планировщик остановлен."; }
        catch (Exception ex) { SchedulerStatus.Text = "Планировщик остановлен с ошибкой: " + ex.Message; }
        finally
        {
            schedulerCancellation.Dispose(); schedulerCancellation = null;
            schedulingTask = null; schedulerStopping = false;
            UpdateRunControls();
        }
    }

    private void StopSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (schedulerCancellation is null || schedulerStopping) return;
        schedulerStopping = true;
        SchedulerStatus.Text = "Планировщик останавливается; текущая автоматическая операция отменяется.";
        schedulerCancellation.Cancel();
        UpdateRunControls();
    }

    private void CancelRun_Click(object sender, RoutedEventArgs e)
    {
        activeRun?.Cancel();
        CancelRunButton.IsEnabled = false;
        Status.Text = "Останавливается операция. Дождитесь записи результата.";
    }

    private void ExternalMonitoring_Click(object sender, RoutedEventArgs e)
    {
        if (agentSettings is null || activeRun is not null || copyVerification is not null || schedulingTask is not null || manualPending) return;
        new ExternalMonitoringWindow(agentSettings) { Owner = this }.ShowDialog();
    }

    private (CheckBox Box, WeekDays Day)[] DayControls =>
    [ (Mon, WeekDays.Monday), (Tue, WeekDays.Tuesday), (Wed, WeekDays.Wednesday), (Thu, WeekDays.Thursday),
      (Fri, WeekDays.Friday), (Sat, WeekDays.Saturday), (Sun, WeekDays.Sunday) ];

    private WeekDays SelectedDays() => DayControls.Where(c => c.Box.IsChecked == true)
        .Aggregate(WeekDays.None, (days, c) => days | c.Day);

    private void SetDays(WeekDays days)
    {
        foreach (var control in DayControls) control.Box.IsChecked = days.HasFlag(control.Day);
    }

    private void Schedule_Changed(object sender, RoutedEventArgs e) => UpdateSchedulePreview();

    private void UpdateSchedulePreview()
    {
        if (NextRun is null || Enabled is null || Schedule.SelectedValue is not ScheduleKind kind) return;
        DaysPanel.Visibility = kind == ScheduleKind.Weekly ? Visibility.Visible : Visibility.Collapsed;
        TimePanel.Visibility = kind == ScheduleKind.Manual ? Visibility.Collapsed : Visibility.Visible;
        if (Enabled.IsChecked != true) { NextRun.Text = "Автоматические запуски отключены."; return; }
        if (kind == ScheduleKind.Manual) { NextRun.Text = "Запуск только вручную."; return; }
        if (!TimeOnly.TryParseExact(DailyAt.Text.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
        { NextRun.Text = "Укажите время в формате чч:мм."; return; }
        try
        {
            var job = new BackupJob { DailyAt = at, TimeZoneId = Zone.Text.Trim(), ScheduleKind = kind, DaysOfWeek = SelectedDays() };
            var errors = job.ValidateSchedule();
            if (errors.Count != 0) { NextRun.Text = string.Join(" ", errors); return; }
            var next = BackupSchedule.NextRun(job, DateTimeOffset.UtcNow)!.Value;
            NextRun.Text = "Следующий запуск по расписанию: " + TimeZoneInfo.ConvertTime(next,
                TimeZoneInfo.FindSystemTimeZoneById(job.TimeZoneId)).ToString("dd.MM.yyyy HH:mm zzz", CultureInfo.InvariantCulture);
        }
        catch (ArgumentException ex) { NextRun.Text = ex.Message.Split(Environment.NewLine)[0]; }
        catch (InvalidOperationException ex) { NextRun.Text = ex.Message; }
    }

    private void Storage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FtpPanel is not null)
            FtpPanel.Visibility = Storage.SelectedValue is StorageKind.Ftp ? Visibility.Visible : Visibility.Collapsed;
        if (DestinationBrowseButton is not null)
            DestinationBrowseButton.Visibility = Storage.SelectedValue is StorageKind.Ftp ? Visibility.Collapsed : Visibility.Visible;
    }

    private void FtpCredentials_Click(object sender, RoutedEventArgs e)
    {
        if (!ready || !canSave || activeRun is not null || copyVerification is not null || manualPending || schedulingTask is not null) return;
        var key = FtpCredentialKey.Text.Trim();
        using var lease = AcquireEngineOrReport();
        if (lease is null) return;
        if (string.IsNullOrWhiteSpace(key))
        {
            key = "ftp-" + (editingId ?? Guid.NewGuid()).ToString("N");
            FtpCredentialKey.Text = key;
        }
        if (new FtpCredentialsWindow(key, ftpCredentials) { Owner = this }.ShowDialog() == true)
            Status.Text = "Учётные данные сохранены в Windows. Сохраните задание с этим идентификатором; доступ к FTP проверяется при выполнении.";
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!canSave || editingId is not Guid id) return;
        if (MessageBox.Show("Удалить настройки задания?", "1C-Backup", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        if (await PersistAsync(jobs.Where(j => j.Id != id).ToList())) { New_Click(sender, e); Status.Text = "Задание удалено."; }
    }
}
