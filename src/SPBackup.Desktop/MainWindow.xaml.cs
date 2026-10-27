using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using System.Text.Json;
using Microsoft.Win32;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow : Window
{
    internal bool SmokeReady => initializationCompleted && ready && !persisting && !jobFileTransfer && !copiesRefreshing && activeRun is null && !manualPending && copyVerification is null;
    private bool initializationCompleted;
    private readonly JobStore store = new(Path.Combine(DesktopPaths.DataDirectory, "jobs.json"), useLocalTime: true);
    private List<BackupJob> jobs = [];
    private readonly AgentSettingsStore agentSettingsStore = new(Path.Combine(DesktopPaths.DataDirectory, "agent.json"));
    private readonly AgentConfigurationCache agentCache = new(Path.Combine(DesktopPaths.DataDirectory, "server-jobs.json"));
    private AgentSettings? agentSettings;
    private Guid? editingId;
    private bool canSave;
    private bool persisting;
    private readonly BackupRunHistory history = new(Path.Combine(DesktopPaths.DataDirectory, "runs.json"));
    private readonly LocalBackupRunner runner;
    private readonly BackupCatalog catalog;
    private readonly BackupVerificationState verificationState;
    private readonly RoutedBackupStorage backupStorage;
    private readonly BackupEngineCoordinator engine;
    private readonly LocalAgentControlStore backgroundControl;
    private readonly DispatcherTimer backgroundTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private LocalAgentState? backgroundState;
    private bool backgroundBusy, backgroundStarting, refreshingBackground;
    private Process? launchedAgent;
    private DateTimeOffset lastBackgroundRefresh;
    private CancellationTokenSource? copyVerification;
    private bool checkingStorages;
    private Guid? storageCheckJobId;
    private bool copiesRefreshing;
    private int copiesRefreshVersion;
    private CancellationTokenSource? activeRun;
    private Guid? activeJobId;
    private readonly LocalBackupDispatcher dispatcher;
    private CancellationTokenSource? schedulerCancellation;
    private Task? schedulingTask;
    private bool schedulerStopping;
    private bool manualPending;
    private bool ready;
    private bool historyRefreshing;
    private List<BackupRun> historyRuns = [];
    private readonly WindowsFtpCredentialStore ftpCredentials = new();
    private string? archivePasswordKey;

    public MainWindow()
    {
        InitializeComponent();
        ThemePicker.SelectedValue = DesktopTheme.Preference.ToString();
        AppVersion.Text = ApplicationBuildInfo.Version;
        AppVersion.ToolTip = ApplicationBuildInfo.SupportText;
        BuildInfo.Text = ApplicationBuildInfo.SupportText;
        AdditionalStorageList.ItemsSource = additionalDestinations;
        additionalDestinations.CollectionChanged += (_, _) => AdditionalStorageOptions.Header =
            $"Копировать также в другие хранилища ({additionalDestinations.Count})";
        JobEditor.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler(EditorFieldChanged));
        JobEditor.AddHandler(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent, new SelectionChangedEventHandler(EditorFieldChanged));
        JobEditor.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler(EditorFieldChanged));
        JobEditor.AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler(EditorFieldChanged));
        var dataDirectory = DesktopPaths.DataDirectory;
        engine = new BackupEngineCoordinator(dataDirectory);
        backgroundControl = new LocalAgentControlStore(dataDirectory);
        backupStorage = new RoutedBackupStorage(ftpCredentials, Path.Combine(dataDirectory, "ftp-verification"));
        catalog = new BackupCatalog(Path.Combine(dataDirectory, "copies.json"));
        verificationState = new BackupVerificationState(Path.Combine(dataDirectory, "verification-state.json"));
        runner = new LocalBackupRunner(new DatabaseBackupExecutor(new BackupProcessRunner()),
            new BackupCompletion(backupStorage, catalog),
            history, Path.Combine(dataDirectory, "work"), progress: value => Dispatcher.BeginInvoke(new Action(() => RenderBackupProgress(value))));
        dispatcher = new LocalBackupDispatcher(store, (job, token) =>
            Dispatcher.InvokeAsync(() => ExecuteJobAsync(job, token)).Task.Unwrap());
        Closing += WindowClosingToTray;
        SourceInitialized += (_, _) => FitInitialWindowToWorkArea();
        AgentModePicker.ItemsSource = new Dictionary<AgentMode, string>
        {
            [AgentMode.Standalone] = "Автономный агент",
            [AgentMode.Managed] = "Управление через сервер"
        };
        AgentModePicker.SelectedValue = AgentMode.Standalone;
        FullBackupDayPicker.ItemsSource = new Dictionary<int, string>
        {
            [-1] = "По числу архивов", [1] = "Понедельник", [2] = "Вторник", [3] = "Среда",
            [4] = "Четверг", [5] = "Пятница", [6] = "Суббота", [0] = "Воскресенье"
        };
        FullBackupDayPicker.SelectedValue = -1;
        Kind.ItemsSource = Enum.GetValues<DatabaseKind>();
        Kind.SelectedItem = DatabaseKind.OneCFile;
        OneCMethodPicker.ItemsSource = new Dictionary<OneCBackupMethod, string>
        {
            [OneCBackupMethod.PlatformDump] = "Выгрузка через 1С (.dt)",
            [OneCBackupMethod.FileArchive] = "Архив базы (ZIP)"
        };
        OneCMethodPicker.SelectedValue = OneCBackupMethod.PlatformDump;
        Storage.ItemsSource = new Dictionary<StorageKind, string>
        {
            [StorageKind.LocalDirectory] = "Локальный диск",
            [StorageKind.NetworkShare] = "Сетевая папка (UNC)",
            [StorageKind.Ftp] = "FTP / FTPS",
            [StorageKind.GoogleDrive] = "Google Drive",
            [StorageKind.RemoteGateway] = "Шлюз хранилища (Proxmox)"
        };
        Storage.SelectedValue = StorageKind.LocalDirectory;
        Schedule.ItemsSource = new Dictionary<ScheduleKind, string>
        {
            [ScheduleKind.Manual] = "Только вручную",
            [ScheduleKind.Daily] = "Ежедневно",
            [ScheduleKind.Weekly] = "По выбранным дням недели",
            [ScheduleKind.SystemStartup] = "Копировать при запуске системы"
        };
        Schedule.SelectedValue = ScheduleKind.Daily;
        Mon.IsChecked = true;

        SaveButton.IsEnabled = DeleteButton.IsEnabled = JobEditor.IsEnabled = false;
        Loaded += async (_, _) =>
        {
            InitializeTray();
            if (Application.Current is App { StartInTray: true } && TrayVisible) Hide();
            else ShowInTaskbar = true;
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
                try { CurrentUserAgentStartup.UpgradeOwnedLegacyEntry(); AgentStartup.IsChecked = CurrentUserAgentStartup.IsEnabled(); }
                catch (Exception ex) { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex); StartupStatus.Text = "Не удалось прочитать автозапуск: " + ex.Message; }
                await RefreshBackgroundStateAsync();
                backgroundTimer.Start();
                UpdateRunControls();
                await InitializeServiceAsync();
                await InitializeRemoteHubAsync();
                if (!SmokeMode) hubTask = new SPBackup.Remote.EmbeddedHub(DesktopPaths.DataDirectory).RunAsync(warning => Dispatcher.BeginInvoke(new Action(() => { if (warning is not null) HubStatus.Text = warning; })), hubShutdown.Token);
                _ = InitializeUpdatesAsync();
                if (Application.Current is App app)
                {
                    if (app.StartAgent && StartBackgroundButton.IsEnabled) StartBackground_Click(this, new RoutedEventArgs());
                    if (app.StartWindowSchedule) StartSchedule_Click(this, new RoutedEventArgs());
                    app.WindowReady();
                }
                initializationCompleted = true;
            }
            catch (Exception ex)
            { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex);
                ShowFromTray();
                canSave = false;
                JobEditor.IsEnabled = SaveButton.IsEnabled = DeleteButton.IsEnabled = RunButton.IsEnabled = false;
                Status.Text = "Не удалось прочитать настройки. Сохранение отключено, чтобы не затереть файл: " + ex.Message;
            }
        };
        backgroundTimer.Tick += async (_, _) => { await RefreshBackgroundStateAsync(); await PollNotificationsAsync(); await PollServerMonitoringAsync(); };
        Closed += (_, _) => { hubShutdown.Cancel(); notificationCancellation.Cancel(); notificationClient.Dispose(); updateTimer.Stop(); updateCancellation?.Cancel(); DisposeTray(); backgroundTimer.Stop(); launchedAgent?.Dispose(); };
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!canSave || !editorOpen || wizardStep != 3) return;
        await SaveEditorAsync();
    }

    private async Task<bool> SaveEditorAsync()
    {
        if (!canSave || !editorOpen || persisting) return false;
        for (var step = 0; step < 3; step++) if (!ValidateWizardStep(step)) return false;
        var scheduleKind = (ScheduleKind)Schedule.SelectedValue;
        TimeOnly[] times = [new(23, 0)];
        if ((scheduleKind is ScheduleKind.Daily or ScheduleKind.Weekly && !TryParseRunTimes(DailyAt.Text, out times))
            || !int.TryParse(KeepLast.Text, out var keep))
        { Status.Text = "Укажите от 1 до 48 уникальных времён в формате чч:мм через запятую и целое число копий."; return false; }
        var storageKind = (StorageKind)Storage.SelectedValue;
        OneCSettings? oneC = null;
        var oneCMethod = OneCMethodPicker.SelectedValue is OneCBackupMethod method ? method : OneCBackupMethod.PlatformDump;
        if (Kind.SelectedItem is DatabaseKind.OneCFile)
        {
            if (!int.TryParse(OneCTimeout.Text, out var timeout))
            {
                if (oneCMethod == OneCBackupMethod.PlatformDump) { Status.Text = "Укажите целое число минут ожидания выгрузки."; return false; }
                timeout = 120;
            }
            oneC = ReadOneCSettings(timeout);
        }
        var ftpPort = 21;
        SqlServerBackupSettings? sql = null;
        PostgreSqlBackupSettings? postgres = null;
        if (Kind.SelectedItem is DatabaseKind.SqlServer)
        {
            if (!int.TryParse(SqlBackupTimeout.Text, out var timeout))
            { Status.Text = "Укажите целое число минут ожидания SQL-копии."; return false; }
            sql = ReadSqlSettings(timeout);
        }
        if (Kind.SelectedItem is DatabaseKind.PostgreSql)
        {
            if (!int.TryParse(PgPort.Text, out var port) || !int.TryParse(PgTimeout.Text, out var timeout))
            { Status.Text = "Укажите целые числа для порта и времени ожидания PostgreSQL."; return false; }
            postgres = ReadPostgreSqlSettings(port, timeout);
        }
        if (storageKind == StorageKind.Ftp && !int.TryParse(FtpPort.Text, out ftpPort))
        { Status.Text = "Укажите целое число в поле порта FTP."; return false; }
        var job = new BackupJob
        {
            Id = editingId ?? Guid.NewGuid(), Name = JobName.Text.Trim(), Kind = (DatabaseKind)Kind.SelectedItem,
            Source = Source.Text.Trim(), DestinationDirectory = Destination.Text.Trim(),
            TemporaryDirectory = string.IsNullOrWhiteSpace(TemporaryFolder.Text) ? null : TemporaryFolder.Text.Trim(),
            AdditionalDestinations = additionalDestinations.Count == 0 ? null : additionalDestinations.ToArray(),
            DailyAt = times[0], AdditionalDailyTimes = times.Length > 1 ? times.Skip(1).ToArray() : null,
            TimeZoneId = BackupSchedule.LocalTimeZone, KeepLast = keep, Enabled = Enabled.IsChecked == true,
            ArchiveCopies = ArchiveCopies.IsChecked == true,
            FileBackupMode = FileBackupModePicker.SelectedValue is FileBackupMode mode ? mode : FileBackupMode.Full,
            FullBackupEvery = int.TryParse(FullBackupEvery.Text, out var every) && every is >= 1 and <= 365 ? every : 7,
            FullBackupDayOfWeek = SelectedFullBackupDay,
            ArchivePasswordKey = EncryptArchive.IsChecked == true ? archivePasswordKey : null, StorageKind = storageKind,
            GoogleDrive = storageKind == StorageKind.GoogleDrive ? googleDriveSettings : null,
            RemoteGateway = storageKind == StorageKind.RemoteGateway ? remoteGatewaySettings : null,
            ScheduleKind = scheduleKind, DaysOfWeek = SelectedDays(),
            OneC = oneC,
            OneCMethod = oneCMethod,
            AllowOpenOneCDatabase = AllowOpenOneCDatabase.IsChecked == true,
            SqlServer = sql, PostgreSql = postgres,
            WebDav = Kind.SelectedItem is DatabaseKind.WebDav ? new WebDavSettings { CredentialKey = webDavCredentialKey ?? "", TimeoutMinutes = int.TryParse(WebDavTimeout.Text, out var davTimeout) ? davTimeout : 120 } : null,
            Ftp = storageKind == StorageKind.Ftp ? new FtpSettings
            {
                Host = FtpHost.Text.Trim(), Port = ftpPort, UseTls = FtpTls.IsChecked == true,
                CredentialKey = FtpCredentialKey.Text.Trim()
            } : null
        };
        var errors = job.Validate();
        if (errors.Count > 0) { Status.Text = string.Join(" ", errors); return false; }
        if (EncryptArchive.IsChecked == true && archivePasswordKey is null)
        { Status.Text = "Сначала задайте пароль архива."; return false; }
        var updated = jobs.Where(j => j.Id != job.Id).Append(job).ToList();
        if (await PersistAsync(updated))
        {
            JobSearch.Clear(); Jobs.SelectedItem = job; CloseEditor();
            Status.Text = "Задание сохранено. Нажмите «Создать копию», чтобы проверить его. Для расписания включите автоматическое копирование.";
            return true;
        }
        return false;
    }

    private async Task<bool> PersistAsync(List<BackupJob> updated)
    {
        persisting = true;
        canSave = false;
        UpdateRunControls();
        try
        {
            // Prepare newly referenced database secrets before the running service can see the new job.
            // Merely entering a password in a discarded draft does not grant service access.
            var knownPgKeys = jobs.Where(job => job.Kind == DatabaseKind.PostgreSql)
                .Select(job => job.PostgreSql?.CredentialKey).OfType<string>().ToHashSet(StringComparer.Ordinal);
            foreach (var key in updated.Where(job => job.Kind == DatabaseKind.PostgreSql)
                .Select(job => job.PostgreSql?.CredentialKey).OfType<string>().Distinct(StringComparer.Ordinal).Where(key => !knownPgKeys.Contains(key)))
                await new WindowsPostgreSqlCredentialStore().ExportForServiceAsync(key);
            var knownSqlKeys = jobs.Where(job => job.Kind == DatabaseKind.SqlServer && job.SqlServer?.Authentication == SqlServerAuthentication.SqlServer)
                .Select(job => job.SqlServer?.CredentialKey).OfType<string>().ToHashSet(StringComparer.Ordinal);
            foreach (var key in updated.Where(job => job.Kind == DatabaseKind.SqlServer && job.SqlServer?.Authentication == SqlServerAuthentication.SqlServer)
                .Select(job => job.SqlServer?.CredentialKey).OfType<string>().Distinct(StringComparer.Ordinal).Where(key => !knownSqlKeys.Contains(key)))
                await new WindowsSqlServerCredentialStore().ExportForServiceAsync(key);
            await store.SaveAsync(updated);
            jobs = updated;
            Jobs.ItemsSource = jobs;
            ApplyJobFilter();
            await RefreshCopiesAsync();
            return true;
        }
        catch (Exception ex) { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex); Status.Text = "Не удалось сохранить: " + ex.Message; return false; }
        finally
        {
            persisting = false;
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
        Status.Text = canSave ? jobs.Count == 0 ? "Начните с первого задания: выберите базу, папку копий и расписание." : "Выберите задание, чтобы создать копию или изменить настройки."
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
        catch (Exception ex) { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex); Status.Text = "Не удалось применить настройки агента: " + ex.Message; }
        finally { UpdateRunControls(); }
    }

    private void Jobs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (editorOpen) { UpdateRunControls(); return; }
        if (!checkingStorages && StorageCheckResults is not null)
            StorageCheckResults.Visibility = Jobs.SelectedItem is BackupJob selected && selected.Id == storageCheckJobId
                ? Visibility.Visible : Visibility.Collapsed;
        if (Jobs.SelectedItem is not BackupJob job) { UpdateRunControls(); return; }
        LoadJobEditor(job);
    }

    private void LoadJobEditor(BackupJob job)
    {
        editingId = job.Id;
        AllowOpenOneCDatabase.IsChecked = job.AllowOpenOneCDatabase;
        ResetOneCBasePicker();
        AutoOneCPlatform.IsChecked = job.OneC?.AutoSelectPlatform ?? true;
        OneCPlatformVersion.Text = job.OneC?.PlatformVersion ?? "";
        OneCExecutable.Text = job.OneC?.ExecutablePath ?? "";
        if (job.Kind == DatabaseKind.OneCFile) _ = RefreshOneCBasesAsync();
        else ++oneCListGeneration;
        OneCTimeout.Text = (job.OneC?.TimeoutMinutes ?? 120).ToString();
        SqlBackupServer.Text = job.SqlServer?.Server ?? "";
        SqlServerDirectory.Text = job.SqlServer?.ServerBackupDirectory ?? "";
        SqlAgentDirectory.Text = job.SqlServer?.AgentReadDirectory ?? "";
        SqlBackupTimeout.Text = (job.SqlServer?.TimeoutMinutes ?? 120).ToString();
        sqlCredentialKey = job.SqlServer?.CredentialKey;
        SqlAuthenticationPicker.SelectedValue = job.SqlServer?.Authentication ?? SqlServerAuthentication.Windows;
        UpdateSqlCredentialStatus();
        ResetPostgreSqlTools();
        PgTlsRequired.IsChecked = job.PostgreSql?.Tls is not null;
        PgTlsRootCertificate.Text = job.PostgreSql?.Tls?.RootCertificatePath ?? "";
        PgHost.Text = job.PostgreSql?.Host ?? "localhost"; PgPort.Text = (job.PostgreSql?.Port ?? 5432).ToString();
        PgUsername.Text = job.PostgreSql?.Username ?? "postgres";
        PgDumpPath.Text = job.PostgreSql?.PgDumpPath ?? ""; PgRestorePath.Text = job.PostgreSql?.PgRestorePath ?? "";
        PgTimeout.Text = (job.PostgreSql?.TimeoutMinutes ?? 120).ToString();
        postgreSqlCredentialKey = job.PostgreSql?.CredentialKey;
        PgAuthenticationPicker.SelectedIndex = postgreSqlCredentialKey is null ? 1 : 0; UpdatePostgreSqlCredentialStatus();
        webDavCredentialKey = job.WebDav?.CredentialKey;
        WebDavTimeout.Text = (job.WebDav?.TimeoutMinutes ?? 120).ToString(); UpdateWebDavCredentialStatus();
        JobName.Text = job.Name; Kind.SelectedItem = job.Kind; Source.Text = job.Source;
        OneCMethodPicker.SelectedValue = job.OneCMethod;
        FileBackupModePicker.SelectedValue = job.FileBackupMode;
        FullBackupDayPicker.SelectedValue = job.FullBackupDayOfWeek is { } fullDay ? (int)fullDay : -1;
        FullBackupEvery.Text = job.FullBackupEvery.ToString(CultureInfo.InvariantCulture);
        googleDriveSettings = job.GoogleDrive;
        remoteGatewaySettings = job.RemoteGateway; UpdateRemoteGatewayStatus();
        googleDriveFolder = job.StorageKind == StorageKind.GoogleDrive ? job.DestinationDirectory : null;
        Destination.Text = job.DestinationDirectory;
        UpdateGoogleDriveStatus();
        TemporaryFolder.Text = job.TemporaryDirectory ?? "";
        additionalDestinations.Clear();
        foreach (var destination in job.AdditionalDestinations ?? []) additionalDestinations.Add(destination);
        DailyAt.Text = string.Join(", ", job.ScheduledTimes.Select(time => time.ToString("HH:mm", CultureInfo.InvariantCulture)));
        KeepLast.Text = job.KeepLast.ToString(); Enabled.IsChecked = job.Enabled;
        archivePasswordKey = job.ArchivePasswordKey;
        EncryptArchive.IsChecked = archivePasswordKey is not null;
        ArchiveCopies.IsChecked = job.ArchiveCopies;
        UpdateArchivePasswordStatus();
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
        if (!NewJobButton.IsEnabled) return;
        selectionBeforeEdit = Jobs.SelectedItem as BackupJob;
        JobSearch.Clear();
        Jobs.SelectedItem = null; editingId = null;
        webDavCredentialKey = null; WebDavTimeout.Text = "120"; UpdateWebDavCredentialStatus();
        JobName.Clear(); Source.Clear(); Destination.Clear(); Kind.SelectedItem = DatabaseKind.OneCFile;
        TemporaryFolder.Clear();
        FileBackupModePicker.SelectedValue = FileBackupMode.Full; FullBackupDayPicker.SelectedValue = -1; FullBackupEvery.Text = "7";
        additionalDestinations.Clear();
        OneCMethodPicker.SelectedValue = OneCBackupMethod.FileArchive;
        AllowOpenOneCDatabase.IsChecked = false;
        ResetOneCBasePicker();
        AutoOneCPlatform.IsChecked = true; OneCPlatformVersion.Clear();
        OneCExecutable.Clear(); OneCTimeout.Text = "120";
        _ = RefreshOneCBasesAsync();
        SqlBackupServer.Clear(); SqlServerDirectory.Clear(); SqlAgentDirectory.Clear(); SqlBackupTimeout.Text = "120";
        sqlCredentialKey = null; SqlAuthenticationPicker.SelectedValue = SqlServerAuthentication.Windows; UpdateSqlCredentialStatus();
        PgTlsRequired.IsChecked = false; PgTlsRootCertificate.Text = "";
        PgHost.Text = "localhost"; PgPort.Text = "5432"; PgUsername.Text = "postgres";
        postgreSqlCredentialKey = null; PgAuthenticationPicker.SelectedIndex = 0; UpdatePostgreSqlCredentialStatus();
        ResetPostgreSqlTools(); PgDumpPath.Clear(); PgRestorePath.Clear(); PgTimeout.Text = "120";
        DailyAt.Text = "23:00"; KeepLast.Text = "7"; Enabled.IsChecked = true;
        archivePasswordKey = null; EncryptArchive.IsChecked = false;
        ArchiveCopies.IsChecked = false;
        UpdateArchivePasswordStatus();
        Storage.SelectedValue = StorageKind.LocalDirectory;
        FtpHost.Clear(); FtpPort.Text = "21"; FtpTls.IsChecked = true; FtpCredentialKey.Clear();
        googleDriveSettings = null; googleDriveFolder = null; UpdateGoogleDriveStatus();
        remoteGatewaySettings = null; UpdateRemoteGatewayStatus();
        Schedule.SelectedValue = ScheduleKind.Daily; SetDays(WeekDays.Monday);
        UpdateSchedulePreview();
        UpdateOneCControls();
        UpdateRunControls();
        OpenEditor();
    }

    private void Kind_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateOneCControls();
    }

    private void OneCMethod_Changed(object sender, SelectionChangedEventArgs e) => UpdateOneCControls();

    private void OpenOneCDatabase_Changed(object sender, RoutedEventArgs e) => UpdateOneCControls();

    private void UpdateOneCControls()
    {
        if (OneCPanel is null || OneCMethodPanel is null || ArchiveCopies is null) return;
        var isOneC = Kind.SelectedItem is DatabaseKind.OneCFile;
        var isLocalFiles = Kind.SelectedItem is DatabaseKind.FilesAndFolders;
        var isWebDav = Kind.SelectedItem is DatabaseKind.WebDav;
        var isFiles = isLocalFiles || isWebDav;
        var fileArchive = isOneC && OneCMethodPicker.SelectedValue is OneCBackupMethod.FileArchive;
        OneCBasePickerPanel.Visibility = isOneC ? Visibility.Visible : Visibility.Collapsed;
        OneCMethodPanel.Visibility = isOneC ? Visibility.Visible : Visibility.Collapsed;
        if (OneCFileAccessPanel is not null) OneCFileAccessPanel.Visibility = fileArchive ? Visibility.Visible : Visibility.Collapsed;
        if (OpenOneCDatabaseWarning is not null)
        {
            OpenOneCDatabaseWarning.Visibility = fileArchive && AllowOpenOneCDatabase.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            OpenOneCDatabaseWarningText.Text = OneCFileArchiveExecutor.OpenDatabaseWarning;
        }
        OneCPanel.Visibility = isOneC && !fileArchive ? Visibility.Visible : Visibility.Collapsed;
        UpdateOneCPlatformControls();
        SqlBackupPanel.Visibility = Kind.SelectedItem is DatabaseKind.SqlServer ? Visibility.Visible : Visibility.Collapsed;
        if (SqlTemporaryHint is not null) SqlTemporaryHint.Visibility = SqlBackupPanel.Visibility;
        PostgresBackupPanel.Visibility = Kind.SelectedItem is DatabaseKind.PostgreSql ? Visibility.Visible : Visibility.Collapsed;
        FileArchiveHint.Visibility = fileArchive && AllowOpenOneCDatabase.IsChecked != true ? Visibility.Visible : Visibility.Collapsed;
        ArchiveCopies.IsEnabled = !fileArchive && !isFiles;
        if (fileArchive || isFiles) ArchiveCopies.IsChecked = true;
        SourceBrowseButton.Visibility = isOneC || isLocalFiles ? Visibility.Visible : Visibility.Collapsed;
        SourceBrowseButton.Content = isFiles ? "Папка…" : "Выбрать…";
        SourceFileBrowseButton.Visibility = isLocalFiles ? Visibility.Visible : Visibility.Collapsed;
        FileBackupPanel.Visibility = isFiles ? Visibility.Visible : Visibility.Collapsed;
        WebDavPanel.Visibility = WebDavOptionsPanel.Visibility = WebDavCredentialsButton.Visibility = isWebDav ? Visibility.Visible : Visibility.Collapsed;
        FileSourceConsistencyHint.Text = isWebDav ? "Копируются файлы выбранной папки, без единого снимка сервера. Избегайте изменений во время копирования." : "Перед копированием закройте программы, изменяющие эти файлы.";
        UpdateFileBackupControls();
        if (SourceLabel is not null) SourceLabel.Text = isWebDav ? "HTTPS-адрес папки WebDAV" : isFiles ? "Исходный файл или папка" : isOneC ? fileArchive ? "Папка или файл базы" : "Папка базы" : "Имя базы данных";
    }

    private void UpdateRunControls()
    {
        if (ExportDiagnosticsButton is not null) ExportDiagnosticsButton.IsEnabled = ready && !jobFileTransfer;
        UpdateTray();
        var idle = !serviceManaging && !installingUpdate && !trayStopping && !persisting && !jobFileTransfer && activeRun is null && !manualPending && !schedulerStopping && copyVerification is null;
        var engineAvailable = !backgroundBusy && !backgroundStarting;
        NewJobButton.IsEnabled = FirstJobButton.IsEnabled = ready && canSave && idle && !editorOpen;
        ImportJobSettingsMenuItem.IsEnabled = NewJobButton.IsEnabled && !editorOpen;
        ExportJobSettingsMenuItem.IsEnabled = ready && !jobFileTransfer && !persisting && !editorOpen && Jobs.SelectedItem is BackupJob;
        FirstJobButton.Visibility = jobs.Count == 0 && JobSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        FirstCopyGuideButton.Visibility = FirstJobButton.Visibility;
        JobActions.Visibility = jobs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EditJobButton.IsEnabled = ready && canSave && idle && !editorOpen && Jobs.SelectedItem is BackupJob;
        DuplicateJobButton.IsEnabled = ready && canSave && idle && !editorOpen && Jobs.SelectedItem is BackupJob;
        var scheduledJob = Jobs.SelectedItem as BackupJob;
        JobScheduleButton.IsEnabled = ready && canSave && idle && !editorOpen && scheduledJob is { ScheduleKind: not ScheduleKind.Manual };
        JobScheduleButton.Content = scheduledJob is { Enabled: true, ScheduleKind: not ScheduleKind.Manual }
            ? "Выключить расписание" : "Включить расписание";
        JobScheduleButton.ToolTip = scheduledJob is null ? "Выберите задание в таблице."
            : !canSave ? "Изменение настроек недоступно в текущем режиме агента."
            : scheduledJob.ScheduleKind == ScheduleKind.Manual ? "Это ручное задание. Задайте время на шаге «Расписание» через «Изменить»."
            : "Изменить расписание только выбранного задания. Планировщик учтёт изменение при следующем обновлении заданий. Копии, уже принятые к выполнению, не отменяются. Для выполнения расписания включите автоматическое копирование.";
        PreviousStepButton.IsEnabled = ready && canSave && idle && wizardStep > 0;
        NextStepButton.IsEnabled = CancelEditorButton.IsEnabled = ready && canSave && idle;
        JobsSummary.Text = $"Всего: {jobs.Count} · По расписанию: {jobs.Count(job => job.Enabled && job.ScheduleKind != ScheduleKind.Manual)}";
        UpdateManualRunControl();
        var canInspectLocally = ready && idle && engineAvailable && schedulingTask is null && !editorOpen && Jobs.SelectedItem is BackupJob;
        CheckStorageButton.IsEnabled = ready && idle && !editorOpen && Jobs.SelectedItem is BackupJob
            && (engineAvailable || BackgroundAcceptsStorageChecks);
        CheckSourceButton.IsEnabled = canInspectLocally;
        CheckSourceButton.ToolTip = !engineAvailable || schedulingTask is not null
            ? "Остановите автоматическое копирование, чтобы проверить источник."
            : "Проверить файл базы или SQL-подключение без создания резервной копии. Для PostgreSQL нужен psql рядом с pg_dump.";
        CheckStorageButton.ToolTip = BackgroundAcceptsStorageChecks
            ? "Проверить хранилища через работающего агента, под его пользователем. Расписание продолжит работать; резервная копия не создаётся."
            : !engineAvailable ? "Дождитесь готовности исполнителя. Если он не поддерживает проверку без остановки расписания, обновите и перезапустите агент/службу."
            : "Проверить хранилища от пользователя окна программы. Доступ службы проверяется через работающего агента.";
        CheckLatestCopiesButton.IsEnabled = canInspectLocally;
        CheckLatestCopiesButton.ToolTip = !engineAvailable || schedulingTask is not null
            ? "Остановите автоматическое копирование, чтобы проверить последние копии."
            : "Проверить размер и SHA-256 последней зарегистрированной копии в каждом хранилище задания. База и сохранённые копии не изменяются.";
        CancelStorageCheckButton.Visibility = checkingStorages ? Visibility.Visible : Visibility.Collapsed;
        CancelStorageCheckButton.IsEnabled = checkingStorages && copyVerification is { IsCancellationRequested: false };
        CancelRunButton.Visibility = activeRun is null ? Visibility.Collapsed : Visibility.Visible;
        ShowLatestCopyButton.IsEnabled = ready && !copiesRefreshing && Jobs.SelectedItem is BackupJob copyJob
            && copyRows.Any(row => row.Record.JobId == copyJob.Id);
        ShowLatestRunButton.IsEnabled = ready && !historyRefreshing && Jobs.SelectedItem is BackupJob runJob
            && historyRuns.Any(run => run.JobId == runJob.Id);
        JobEditor.IsEnabled = SaveButton.IsEnabled = ready && canSave && idle;
        DeleteButton.IsEnabled = ready && canSave && idle && Jobs.SelectedItem is BackupJob;
        AgentModePicker.IsEnabled = SaveAgentSettingsButton.IsEnabled = ready && idle && engineAvailable && schedulingTask is null && !editorOpen;
        SaveAgentSettingsButton.ToolTip = editorOpen ? "Сначала сохраните задание или закройте его редактор." : null;
        ExternalMonitoringButton.IsEnabled = ready && idle && schedulingTask is null;
        StartScheduleButton.IsEnabled = ready && canSave && idle && engineAvailable && schedulingTask is null && !editorOpen;
        ArchivePasswordButton.IsEnabled = ready && canSave && idle && engineAvailable && schedulingTask is null;
        GoogleDriveConnectButton.IsEnabled = ready && canSave && idle && engineAvailable && schedulingTask is null;
        FtpCredentialsButton.IsEnabled = ready && canSave && idle && engineAvailable && schedulingTask is null;
        StopScheduleButton.IsEnabled = schedulingTask is not null && !schedulerStopping;
        RefreshCopiesButton.IsEnabled = ready && !copiesRefreshing;
        VerifyCopyButton.IsEnabled = ready && idle && engineAvailable && schedulingTask is null &&
            Copies.SelectedItem is BackupCopyRow selected && jobs.Any(job => job.Id == selected.Record.JobId);
        ExportCopyButton.IsEnabled = VerifyCopyButton.IsEnabled;
        ExportCopyButton.ToolTip = !engineAvailable || schedulingTask is not null
            ? "Остановите автоматическое копирование, чтобы сохранить выбранную копию на компьютер."
            : "Скачать копию с FTP или скопировать локальный / сетевой файл в выбранную папку. Размер и SHA-256 проверяются; существующие файлы не перезаписываются.";
        ExtractOneCButton.IsEnabled = ready && idle && engineAvailable && schedulingTask is null &&
            Copies.SelectedItem is BackupCopyRow extractable && jobs.Any(job => job.Id == extractable.Record.JobId &&
                CanExtractCopy(extractable.Record, job));
        var isFileSnapshot = Copies.SelectedItem is BackupCopyRow fileCopy && fileCopy.Record.Backup.FileSnapshot is not null;
        RestoreFilesButton.Visibility = isFileSnapshot ? Visibility.Visible : Visibility.Collapsed;
        ExtractOneCButton.Visibility = isFileSnapshot ? Visibility.Collapsed : Visibility.Visible;
        RestoreFilesButton.IsEnabled = isFileSnapshot && VerifyCopyButton.IsEnabled;
        var isIncrement = Copies.SelectedItem is BackupCopyRow { Record.Backup.FileSnapshot.IsFull: false };
        ExportCopyButton.Content = isIncrement ? "Сохранить этот архив…" : "Сохранить копию…";
        if (isIncrement) ExportCopyButton.ToolTip += " Это только изменения: для восстановления нужны полная основа и все предшествующие архивы. Кнопка «Восстановить файлы…» собирает цепочку автоматически.";
        CancelCopyVerificationButton.IsEnabled = copyVerification is { IsCancellationRequested: false };
        CancelCopyVerificationButton.Visibility = copyVerification is not null && !checkingStorages ? Visibility.Visible : Visibility.Collapsed;
        OpenCopyDirectoryButton.IsEnabled = ready && Copies.SelectedItem is BackupCopyRow { CanOpenDirectory: true };
        StartBackgroundButton.IsEnabled = ready && canSave && agentSettings?.Mode == AgentMode.Standalone && idle && engineAvailable && schedulingTask is null && !editorOpen && !backgroundStarting
            && automationService is AutomationService.None or AutomationService.Stopped;
        StopBackgroundButton.IsEnabled = ready && !serviceManaging && (backgroundBusy && backgroundState?.Phase == LocalAgentPhase.Running
            || schedulingTask is not null && !schedulerStopping || automationService == AutomationService.Running);
        var automationStarted = backgroundBusy || schedulingTask is not null || backgroundStarting
            || automationService is AutomationService.Starting or AutomationService.Running or AutomationService.Stopping;
        StartBackgroundButton.Visibility = automationStarted ? Visibility.Collapsed : Visibility.Visible;
        StopBackgroundButton.Visibility = automationStarted ? Visibility.Visible : Visibility.Collapsed;
        ArchivePasswordButton.ToolTip = FtpCredentialsButton.ToolTip = !engineAvailable || schedulingTask is not null
            ? "Для настройки пароля остановите автоматическое копирование." : null;
        SaveStartupButton.IsEnabled = ready;
        RefreshHistoryButton.IsEnabled = ready && !historyRefreshing;
        ExportHistoryButton.IsEnabled = ready && !historyRefreshing && RunHistory.Items.Count != 0;
        OpenRunDirectoryButton.IsEnabled = ready && RunHistory.SelectedItem is BackupRun;
        UpdateSelectedJobSummary();
        UpdateRunGuidance();
        UpdateAutomationOverview();
    }

    private IDisposable? AcquireEngineOrReport()
    {
        try { return engine.Acquire(); }
        catch (Exception ex) { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex); Status.Text = "Исполнение недоступно: " + ex.Message; return null; }
    }

    private async Task RefreshBackgroundStateAsync()
    {
        if (refreshingBackground) return;
        refreshingBackground = true;
        try
        {
            backgroundBusy = engine.IsHeldElsewhere();
            backgroundState = await backgroundControl.ReadAsync();
            UpdateManualRunControl();
            await RefreshManualRequestsAsync();
            await RefreshBackupProgressAsync();
            if (launchedAgent is not null && launchedAgent.HasExited)
            {
                var code = launchedAgent.ExitCode;
                launchedAgent.Dispose(); launchedAgent = null; backgroundStarting = false;
                if (code != 0) Status.Text = $"Фоновый агент завершился с кодом {code}. Проверьте режим и настройки заданий.";
            }
            if (backgroundBusy) backgroundStarting = false;
            automationReadFailed = false;
            automationAutostartUnknown = false;
            if (!SmokeMode)
            {
                try { automationAutostart = CurrentUserAgentStartup.IsEnabled(); }
                catch { automationAutostart = false; automationAutostartUnknown = true; }
            }
            if (backgroundBusy && DateTimeOffset.UtcNow - lastBackgroundRefresh > TimeSpan.FromSeconds(10))
            {
                lastBackgroundRefresh = DateTimeOffset.UtcNow;
                await RefreshHistoryAsync(); await RefreshCopiesAsync();
            }
        }
        catch (Exception ex) { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex); automationReadFailed = true; backgroundState = null; }
        finally { refreshingBackground = false; RefreshService(); UpdateRunControls(); }
    }

    private async void StartBackground_Click(object sender, RoutedEventArgs e)
    {
        if (!StartBackgroundButton.IsEnabled) return;
        var executable = Path.Combine(AppContext.BaseDirectory, "SPBackup.Agent.exe");
        if (!File.Exists(executable)) { Status.Text = "Фоновый агент не найден рядом с приложением. Используйте полный дистрибутив Windows."; return; }
        try
        {
            if (OwnService() is not null)
            {
                WindowsAgentService.Start(executable, DesktopPaths.DataDirectory);
                await RefreshBackgroundStateAsync(); return;
            }
            if (WindowsAgentService.QueryInfo() is not null) throw new InvalidOperationException("Служба относится к другому профилю. Остановите или удалите её перед запуском другого агента.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--run"); start.ArgumentList.Add("--quiet");
            start.ArgumentList.Add("--data-dir");
            start.ArgumentList.Add(DesktopPaths.DataDirectory);
            backgroundStarting = true; UpdateRunControls();
            launchedAgent = Process.Start(start) ?? throw new InvalidOperationException("Не удалось создать процесс агента.");
            await RefreshBackgroundStateAsync();
        }
        catch (Exception ex) { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex); backgroundStarting = false; Status.Text = "Не удалось запустить агент: " + ex.Message; UpdateRunControls(); }
    }

    private async void StopBackground_Click(object sender, RoutedEventArgs e)
    {
        if (schedulingTask is not null) { StopSchedule_Click(sender, e); return; }
        if (!StopBackgroundButton.IsEnabled) return;
        try
        {
            if (!SmokeMode && OwnService() is { State: not 1 } && (!backgroundBusy || backgroundState?.RunsSchedules != false))
            {
                await StopOwnedServiceAsync(); await RefreshBackgroundStateAsync(); return;
            }
            if (backgroundState is null) return;
            await backgroundControl.RequestStopAsync(backgroundState.SessionId);
            BackgroundStatus.Text = "Запрошена остановка агента. Дождитесь завершения операции.";
            StopBackgroundButton.IsEnabled = false;
        }
        catch (Exception ex) { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex); Status.Text = "Не удалось остановить агент: " + ex.Message; }
    }

    private void EnableStartupForSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        try
        {
            if (agentSettings?.Mode != AgentMode.Standalone)
                throw new InvalidOperationException("Сначала сохраните автономный режим в настройках агента.");
            CurrentUserAgentStartup.Save(true);
            AgentStartup.IsChecked = true;
            StartupScheduleStatus.Text = "Автозапуск включён. Агент запустится при следующем входе в Windows. Задание нужно сохранить отдельно.";
            StartupStatus.Text = "Агент будет запускаться при входе текущего пользователя в Windows.";
        }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex);
            StartupScheduleStatus.Text = "Не удалось включить автозапуск: " + ex.Message;
        }
    }

    private void SaveStartup_Click(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        try
        {
            if (AgentStartup.IsChecked == true && agentSettings?.Mode != AgentMode.Standalone)
                throw new InvalidOperationException("Автозапуск фонового агента доступен в автономном режиме. Сначала сохраните этот режим в настройках агента.");
            CurrentUserAgentStartup.Save(AgentStartup.IsChecked == true);
            StartupStatus.Text = AgentStartup.IsChecked == true ? "Агент будет запускаться при входе текущего пользователя в Windows. После переноса папки программы сохраните настройку заново."
                : "Автозапуск этой программы выключен. Уже работающий агент можно остановить кнопкой «Остановить агент».";
        }
        catch (Exception ex) { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex); StartupStatus.Text = "Не удалось изменить автозапуск: " + ex.Message; }
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
        JobsEmptyHint.Text = query.Length == 0 ? "Начните с пробной копии обычной папки.\nИнструкция поможет создать задание и проверить восстановление." : "Измените или очистите строку поиска.";
        UpdateRunControls();
    }

    private void BrowsePath_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string target }) return;
        var field = target switch
        {
            "Source" or "SourceFile" => Source, "Destination" => Destination, "OneCExecutable" => OneCExecutable,
            "TemporaryFolder" => TemporaryFolder,
            "SqlAgentDirectory" => SqlAgentDirectory, "PgDumpPath" => PgDumpPath, "PgRestorePath" => PgRestorePath,
            _ => null
        };
        if (field is null) return;
        try
        {
            var fileSource = target == "Source" && Kind.SelectedItem is DatabaseKind.OneCFile && OneCMethodPicker.SelectedValue is OneCBackupMethod.FileArchive;
            if (fileSource || target is "SourceFile" or "OneCExecutable" or "PgDumpPath" or "PgRestorePath")
            {
                var dialog = new OpenFileDialog
                {
                    Title = target == "SourceFile" ? "Выберите файл для копирования" : fileSource ? "Выберите файл базы 1Cv8.1CD" : "Выберите исполняемый файл",
                    Filter = target == "SourceFile" ? "Все файлы|*.*" : fileSource ? "Файл базы 1С (1Cv8.1CD)|1Cv8.1CD" : "Программы (*.exe)|*.exe",
                    CheckFileExists = true,
                    Multiselect = false
                };
                if (dialog.ShowDialog(this) == true) field.Text = dialog.FileName;
            }
            else
            {
                var dialog = new OpenFolderDialog { Title = target == "TemporaryFolder" ? "Выберите временную папку на HDD" : "Выберите каталог", Multiselect = false };
                if (dialog.ShowDialog(this) == true) field.Text = dialog.FolderName;
            }
        }
        catch (Exception ex) { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex); Status.Text = "Не удалось выбрать путь: " + ex.Message; }
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
            "history" => ("История запусков", "Результаты запусков и ошибки. Время — на этом компьютере."),
            "copies" => ("Резервные копии", "Зарегистрированные копии всех заданий, расположение файлов и проверка целостности."),
            "agent" => ("Настройки программы", "Основное, фоновая работа, обновления, подключения и помощь."),
            _ => ("Задания резервного копирования", "Файлы и папки · WebDAV · 1С · MS SQL · PostgreSQL")
        };
        UpdateAutomationLayout();
    }

    private async Task RefreshHistoryAsync()
    {
        if (historyRefreshing) return;
        historyRefreshing = true;
        UpdateRunControls();
        try
        {
            historyRuns = (await history.ReadAsync()).OrderByDescending(r => r.StartedUtc).ToList();
            LatestJobRuns = JobRunOverview.From(historyRuns);
            var selectedId = (RunHistory.SelectedItem as BackupRun)?.RunId;
            RunHistory.ItemsSource = historyRuns;
            ApplyHistoryFilter();
            RunHistory.SelectedItem = RunHistory.Items.OfType<BackupRun>().FirstOrDefault(run => run.RunId == selectedId);
        }
        catch { LatestJobRuns = JobRunOverview.Unavailable; throw; }
        finally { historyRefreshing = false; UpdateRunControls(); }
    }

    private void HistorySearch_Changed(object sender, TextChangedEventArgs e) => ApplyHistoryFilter();
    private void HistoryFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyHistoryFilter();

    private void ApplyHistoryFilter()
    {
        if (RunHistory is null || HistorySummary is null || HistorySearch is null) return;
        var query = HistorySearch.Text.Trim();
        var filter = (HistoryStatusFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        var view = CollectionViewSource.GetDefaultView(RunHistory.ItemsSource);
        if (view is not null)
        {
            view.Filter = item => item is BackupRun run && (filter switch
            {
                "problems" => run.Warnings?.Count > 0 || run.Status is BackupRunStatus.Failed or BackupRunStatus.Cancelled or BackupRunStatus.Interrupted or BackupRunStatus.PartiallySucceeded,
                "succeeded" => run.Status == BackupRunStatus.Succeeded,
                "running" => run.Status == BackupRunStatus.Running,
                _ => true
            }) && (query.Length == 0 || run.JobName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || run.Details.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || run.WorkDirectory.Contains(query, StringComparison.CurrentCultureIgnoreCase));
            view.Refresh();
        }
        if (RunHistory.SelectedItem is BackupRun selected && !RunHistory.Items.Contains(selected)) RunHistory.SelectedItem = null;
        HistorySummary.Text = $"Показано: {RunHistory.Items.Count} из {historyRuns.Count}. Время — на этом компьютере. Экспорт включает выбранные фильтром записи.";
        if (ExportHistoryButton is not null) ExportHistoryButton.IsEnabled = ready && !historyRefreshing && RunHistory.Items.Count != 0;
    }

    private async void RefreshHistory_Click(object sender, RoutedEventArgs e)
    {
        try { if (ready) await RefreshHistoryAsync(); }
        catch (Exception ex) { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex); Status.Text = "Не удалось обновить историю: " + ex.Message; }
    }

    private void RunHistory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RunDetails.Text = RunHistory.SelectedItem is BackupRun run ?
            $"Задание: {run.JobName}\nРезультат: {run.StatusDescription}\n"
            + (run.Error is null ? "" : "Ошибка: " + run.Error + "\n")
            + (run.Warnings?.Count > 0 ? "Предупреждения:\n" + string.Join("\n", run.Warnings) + "\n" : "")
            + (run.Destinations is null ? "" : "Доставка по хранилищам:\n" + string.Join("\n", run.Destinations.Select(destination =>
                destination.Destination + ": " + (destination.Record is null ? destination.Error : "Сохранено · " + destination.Record.Backup.Location))) + "\n")
            + (run.Backup is null ? "" : $"Копия: {run.Backup.Location}\nРазмер: {run.Backup.Length} байт\n")
            + $"Начало: {run.StartedUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}\n"
            + $"Окончание: {run.FinishedUtc?.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") ?? "ещё выполняется"}\n"
            + $"Рабочая папка: {run.WorkDirectory}\nID запуска: {run.RunId}\n"
            + (run.Backup is null ? "" : $"SHA256: {run.Backup.Sha256}\n")
            : "Выберите запуск в таблице.";
        UpdateRunControls();
    }

    internal async Task ExportHistoryAsync(string path)
    {
        var profile = Path.GetFullPath(DesktopPaths.DataDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(path);
        if (target.StartsWith(profile, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Выберите папку вне рабочего профиля приложения, чтобы не перезаписать его настройки или журналы.");
        var snapshot = RunHistory.Items.OfType<BackupRun>().ToArray();
        var document = new { Application = "SP Backup", FormatVersion = 1, ExportedUtc = DateTimeOffset.UtcNow,
            TimeZone = "UTC", Runs = snapshot };
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async void ExportHistory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "Экспорт истории запусков", Filter = "Журнал JSON (*.json)|*.json",
            FileName = $"SP-Backup-history-{DateTime.Now:yyyyMMdd-HHmmss}.json", DefaultExt = ".json", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try { await ExportHistoryAsync(dialog.FileName); Status.Text = "История экспортирована: " + dialog.FileName; }
        catch (Exception ex) { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex); Status.Text = "Не удалось экспортировать историю: " + ex.Message; }
    }

    private async void OpenRunDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (RunHistory.SelectedItem is not BackupRun run) return;
        try
        {
            if (!await Task.Run(() => Directory.Exists(run.WorkDirectory)))
            { Status.Text = "Рабочая папка недоступна. После успешного копирования временные файлы удаляются: " + run.WorkDirectory; return; }
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add(run.WorkDirectory);
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex); Status.Text = "Не удалось открыть рабочую папку: " + ex.Message; }
    }

    private async Task RefreshCopiesAsync()
    {
        var version = ++copiesRefreshVersion;
        copiesRefreshing = true;
        UpdateRunControls();
        try
        {
            var records = await Task.Run(() => catalog.ReadAsync());
            IReadOnlyList<BackupVerificationEntry> checks = [];
            string? checkReadError = null;
            try { checks = await verificationState.ReadAsync(); }
            catch (Exception ex) { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex); checkReadError = ex.Message; }
            if (version != copiesRefreshVersion) return;
            copyCheckReadError = checkReadError;
            var rows = records.OrderByDescending(record => record.CompletedUtc).Select(record =>
                new BackupCopyRow(record, jobs.FirstOrDefault(job => job.Id == record.JobId)?.Name
                    ?? $"Задание недоступно ({record.JobId.ToString("N")[..8]})",
                    checks.FirstOrDefault(check => check.Matches(record)), copyCheckReadError is not null)).ToList();
            var selectedId = (Copies.SelectedItem as BackupCopyRow)?.Record.Id;
            copyRows = rows;
            Copies.ItemsSource = rows;
            UpdateCopyJobChoices();
            ApplyCopyFilter();
            Copies.SelectedItem = Copies.Items.OfType<BackupCopyRow>().FirstOrDefault(row => row.Record.Id == selectedId);
        }
        catch (Exception ex)
        { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex);
            if (version != copiesRefreshVersion) return;
            Copies.ItemsSource = null;
            copyRows = [];
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

    private void Copies_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (copyVerification is null) CopyVerificationStatus.Text = "";
        CopyDetails.Visibility = Copies.SelectedItem is BackupCopyRow ? Visibility.Visible : Visibility.Collapsed;
        SelectedCopyPath.Text = Copies.SelectedItem is BackupCopyRow selectedCopy ? selectedCopy.Record.Backup.FileSnapshot is { } fileSnapshot
            ? (fileSnapshot.IsIncomplete ? "Неполная копия · " : "") + (fileSnapshot.IsFull ? "Полная копия · 1 архив"
                : $"Инкрементная копия · архивов для восстановления: {fileSnapshot.Depth + 1}") + " · " + selectedCopy.DisplayLocation
            : selectedCopy.DisplayLocation : "";
        CopyDetails.Text = Copies.SelectedItem is BackupCopyRow row
            ? $"{row.JobName}\n{row.DisplayLocation}\nРазмер: {row.Record.Backup.Length} байт\nШифрование: {row.EncryptionDescription}\nSHA256: {row.Sha256}\n{FileSnapshotDetails(row.Record.Backup.FileSnapshot)}\n{row.CheckHelp}" : "";
        UpdateRunControls();
    }

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
        CopyVerificationStatus.Text = row.Record.Backup.FileSnapshot is null ? "Проверяется размер и SHA256: " + row.DisplayLocation
            : "Проверяется размер и SHA-256 всех архивов файловой цепочки: " + row.DisplayLocation;
        BackupVerificationOutcome outcome;
        string? error = null;
        try
        {
            var checkedArchives = await Task.Run(() => new BackupPointVerifier(backupStorage, catalog).VerifyAsync(row.Record, job, cancellation.Token));
            outcome = BackupVerificationOutcome.Verified;
            CopyVerificationStatus.Text = row.Record.Backup.FileSnapshot is null
                ? "Размер и SHA256 совпадают с журналом: " + row.DisplayLocation + ". Проверка не заменяет пробное восстановление базы."
                : $"Файловая цепочка проверена: архивов {checkedArchives}, размер и SHA-256 совпадают. Для проверки содержимого используйте «Восстановить файлы…».";
            if (row.Record.Backup.FileSnapshot?.IsIncomplete == true)
                CopyVerificationStatus.Text += "\n" + BackupCopyRow.IncompleteWarning;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { outcome = BackupVerificationOutcome.Cancelled; CopyVerificationStatus.Text = "Проверка отменена: " + row.DisplayLocation; }
        catch (Exception ex)
        { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex); outcome = BackupVerificationOutcome.Failed; error = ex.Message; CopyVerificationStatus.Text = "Проверка не пройдена: " + row.DisplayLocation + ". " + ex.Message; }
        try
        {
            var message = CopyVerificationStatus.Text;
            CopyVerificationStatus.Text = message + await RememberChecksAsync([BackupVerificationEntry.Create(row.Record,
                outcome, BackupVerificationOrigin.Manual, DateTimeOffset.UtcNow, error)]);
        }
        finally { copyVerification = null; UpdateRunControls(); }
    }

    private void CancelCopyVerification_Click(object sender, RoutedEventArgs e)
    {
        copyVerification?.Cancel();
        if (checkingStorages)
            StorageCheckResults.Header = Status.Text = checkingLatestCopies
                ? "Отмена проверки копий. Дождитесь остановки чтения и очистки временных файлов FTP."
                : "Отмена проверки. Дождитесь очистки тестовых файлов.";
        UpdateRunControls();
    }

    private async void ExtractOneC_Click(object sender, RoutedEventArgs e)
    {
        if (!ExtractOneCButton.IsEnabled) return;
        string? passwordOverride = null;
        if (Copies.SelectedItem is BackupCopyRow { Record.Backup.ArchivePasswordKey: not null })
        {
            var passwordDialog = new ArchivePasswordWindow(restoring: true) { Owner = this };
            if (passwordDialog.ShowDialog() != true) return;
            passwordOverride = passwordDialog.PasswordOverride;
        }
        var dialog = new OpenFolderDialog { Title = "Выберите родительский каталог: база будет извлечена в новую папку" };
        if (dialog.ShowDialog(this) == true) await ExtractSelectedCopyAsync(dialog.FolderName, passwordOverride);
    }

    internal async Task ExtractSelectedCopyAsync(string parentDirectory, string? passwordOverride = null)
    {
        if (!ExtractOneCButton.IsEnabled || Copies.SelectedItem is not BackupCopyRow row) return;
        var job = jobs.FirstOrDefault(job => job.Id == row.Record.JobId);
        if (job is null) return;
        using var lease = AcquireEngineOrReport();
        if (lease is null) { CopyVerificationStatus.Text = Status.Text; return; }
        using var cancellation = new CancellationTokenSource();
        copyVerification = cancellation; UpdateRunControls();
        CopyVerificationStatus.Text = "Проверяется архив и извлекается файл базы в новую папку внутри: " + parentDirectory;
        try
        {
            var extracted = await Task.Run(() => new OneCArchiveExtractor().ExtractAsync(row.Record, job, parentDirectory, cancellation.Token, passwordOverride));
            CopyVerificationStatus.Text = "Файл базы извлечён: " + extracted.Path + ". Добавьте эту папку как отдельную файловую базу в списке 1С и проверьте её работу.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { CopyVerificationStatus.Text = "Извлечение отменено. Исходный архив сохранён."; }
        catch (Exception ex) { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex); CopyVerificationStatus.Text = "Не удалось извлечь базу: " + ex.Message; }
        finally { copyVerification = null; UpdateRunControls(); }
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
        catch (Exception ex) { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex); CopyVerificationStatus.Text = "Не удалось открыть папку: " + ex.Message; }
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (!RunButton.IsEnabled || Jobs.SelectedItem is not BackupJob job) return;
        if (job.Kind == DatabaseKind.OneCFile && job.OneCMethod == OneCBackupMethod.PlatformDump && job.OneC is null)
        { Status.Text = "Укажите путь к 1cv8.exe и сохраните задание перед запуском."; return; }
        await RunManualAsync(job);
    }

    private async Task ExecuteJobAsync(BackupJob job, CancellationToken cancellationToken)
    {
        using var lease = engine.Acquire();
        ClearBackupProgress();
        activeJobId = job.Id;
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
        catch (Exception ex) { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex); Status.Text = "Не удалось выполнить или записать результат: " + ex.Message; }
        finally
        {
            activeRun.Dispose(); activeRun = null;
            activeJobId = null;
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
        catch (Exception ex) { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex); SchedulerStatus.Text = "Планировщик остановлен с ошибкой: " + ex.Message; }
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

    private static bool TryParseRunTimes(string text, out TimeOnly[] times)
    {
        times = [];
        var parts = text.Split([',', ';'], StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 48) return false;
        var parsed = new List<TimeOnly>();
        foreach (var part in parts)
        {
            if (!TimeOnly.TryParseExact(part, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                || parsed.Contains(time)) return false;
            parsed.Add(time);
        }
        times = parsed.Order().ToArray();
        return true;
    }

    private void UpdateSchedulePreview()
    {
        if (NextRun is null || Enabled is null || Schedule.SelectedValue is not ScheduleKind kind) return;
        DaysPanel.Visibility = kind == ScheduleKind.Weekly ? Visibility.Visible : Visibility.Collapsed;
        TimePanel.Visibility = kind is ScheduleKind.Manual or ScheduleKind.SystemStartup ? Visibility.Collapsed : Visibility.Visible;
        Enabled.Visibility = kind == ScheduleKind.Manual ? Visibility.Collapsed : Visibility.Visible;
        SchedulePreviewHint.Visibility = Visibility.Collapsed;
        if (StartupSchedulePanel is not null) StartupSchedulePanel.Visibility = kind == ScheduleKind.SystemStartup ? Visibility.Visible : Visibility.Collapsed;
        if (kind == ScheduleKind.Manual) { NextRun.Text = "Запуск только вручную."; return; }
        if (Enabled.IsChecked != true) { NextRun.Text = "Автоматические запуски отключены. Чтобы увидеть ближайшие запуски, включите «Выполнять задание по расписанию»."; return; }
        if (kind == ScheduleKind.SystemStartup)
        {
            NextRun.Text = "Одна попытка за загрузку системы после запуска агента. До входа в Windows агент не работает. Перезапуск программы и выход из сна не повторяют копию. После ошибки повторите вручную.";
            return;
        }
        if (!TryParseRunTimes(DailyAt.Text, out var times))
        { NextRun.Text = "Укажите от 1 до 48 уникальных времён в формате чч:мм через запятую."; return; }
        try
        {
            var job = new BackupJob { DailyAt = times[0], AdditionalDailyTimes = times.Skip(1).ToArray(),
                TimeZoneId = BackupSchedule.LocalTimeZone, ScheduleKind = kind, DaysOfWeek = SelectedDays() };
            var errors = job.ValidateSchedule();
            if (errors.Count != 0) { NextRun.Text = string.Join(" ", errors); return; }
            var after = DateTimeOffset.UtcNow;
            var upcoming = new List<string>();
            for (var index = 0; index < 3; index++)
            {
                var next = BackupSchedule.NextRun(job, after)!.Value;
                upcoming.Add(next.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture));
                after = next;
            }
            NextRun.Text = "Ближайшие 3 запуска по местному времени:\n" + string.Join("\n", upcoming);
            SchedulePreviewHint.Visibility = Visibility.Visible;
        }
        catch (ArgumentException ex) { NextRun.Text = ex.Message.Split(Environment.NewLine)[0]; }
        catch (InvalidOperationException ex) { NextRun.Text = ex.Message; }
    }

    private void Storage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FtpPanel is not null)
            FtpPanel.Visibility = Storage.SelectedValue is StorageKind.Ftp ? Visibility.Visible : Visibility.Collapsed;
        if (DestinationBrowseButton is not null)
            DestinationBrowseButton.Visibility = Storage.SelectedValue is StorageKind.Ftp or StorageKind.GoogleDrive or StorageKind.RemoteGateway ? Visibility.Collapsed : Visibility.Visible;
        if (GoogleDrivePanel is not null)
        {
            var cloud = Storage.SelectedValue is StorageKind.GoogleDrive;
            if (cloud && googleDriveFolder is not null) Destination.Text = googleDriveFolder;
            else if (!cloud && googleDriveFolder is not null && Destination.Text == googleDriveFolder) Destination.Clear();
            GoogleDrivePanel.Visibility = cloud ? Visibility.Visible : Visibility.Collapsed;
            StorageDestinationLabel.Visibility = StorageDestinationPanel.Visibility = cloud || Storage.SelectedValue is StorageKind.RemoteGateway ? Visibility.Collapsed : Visibility.Visible;
            UpdateGoogleDriveStatus();
        }
        if (RemoteGatewayPanel is not null) { RemoteGatewayPanel.Visibility = Storage.SelectedValue is StorageKind.RemoteGateway ? Visibility.Visible : Visibility.Collapsed; UpdateRemoteGatewayStatus(); }
    }

    private void UpdateArchivePasswordStatus()
    {
        if (ArchivePasswordStatus is null) return;
        ArchivePasswordStatus.Text = archivePasswordKey is null ? "Пароль пока не задан." : "Для задания задан пароль. Смена пароля действует на новые копии.";
    }

    private void Encryption_Changed(object sender, RoutedEventArgs e)
    {
        if (ArchiveCopies is not null && EncryptArchive.IsChecked == true) ArchiveCopies.IsChecked = true;
        UpdateArchivePasswordStatus();
    }

    private void ArchivePassword_Click(object sender, RoutedEventArgs e)
    {
        if (!ArchivePasswordButton.IsEnabled) return;
        using var lease = AcquireEngineOrReport();
        if (lease is null) return;
        var dialog = new ArchivePasswordWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;
        archivePasswordKey = dialog.NewKey;
        EditorFieldChanged(sender, e);
        EncryptArchive.IsChecked = true;
        UpdateArchivePasswordStatus();
        Status.Text = "Новый пароль сохранён в Windows. Сохраните задание, чтобы использовать его для новых копий.";
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
        }
        if (new FtpCredentialsWindow(key, ftpCredentials) { Owner = this }.ShowDialog() == true)
        {
            FtpCredentialKey.Text = key;
            EditorFieldChanged(sender, e);
            Status.Text = "Логин и пароль FTP сохранены в Windows. Сохраните задание; соединение проверяется при создании копии.";
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!canSave || editingId is not Guid id) return;
        if (MessageBox.Show("Удалить настройки задания?", "SP Backup", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        if (await PersistAsync(jobs.Where(j => j.Id != id).ToList())) { Jobs.SelectedItem = null; editingId = null; CloseEditor(); Status.Text = "Задание удалено. Резервные копии и история сохранены."; }
    }
}
