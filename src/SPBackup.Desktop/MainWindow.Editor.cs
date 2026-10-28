using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private int wizardStep;
    private bool editorOpen;
    private BackupJob? selectionBeforeEdit;
    private string? copiedSourceName;

    private void EditorFieldChanged(object sender, RoutedEventArgs e)
    {
        if (!editorOpen || persisting || WizardError.Visibility != Visibility.Visible) return;
        WizardError.Visibility = Visibility.Collapsed;
        Status.Text = "Настройка задания. Изменения ещё не сохранены.";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (editorOpen && e.Key == Key.Escape && !persisting)
        { CancelEditor_Click(sender, e); e.Handled = true; }
    }

    private void OpenEditor()
    {
        editorOpen = true;
        EditorTitle.Text = editingId is null ? "Новое задание" : "Изменение задания";
        JobListCard.Visibility = Visibility.Collapsed;
        EditorCard.Visibility = Visibility.Visible;
        SetWizardStep(0);
        RememberEditorDraft();
        UpdateRunControls();
        JobName.Focus();
    }

    private void CloseEditor()
    {
        ++postgresToolSearchGeneration;
        editorOpen = false;
        editorBaseline = null;
        editorContainsImportedDraft = false;
        copiedSourceName = null;
        EditorCard.Visibility = Visibility.Collapsed;
        JobListCard.Visibility = Visibility.Visible;
        WizardError.Visibility = Visibility.Collapsed;
        UpdateRunControls();
    }

    private void EditJob_Click(object sender, RoutedEventArgs e)
    {
        if (!EditJobButton.IsEnabled || Jobs.SelectedItem is not BackupJob job) return;
        selectionBeforeEdit = job;
        LoadJobEditor(job);
        OpenEditor();
    }

    private void DuplicateJob_Click(object sender, RoutedEventArgs e)
    {
        if (!DuplicateJobButton.IsEnabled || Jobs.SelectedItem is not BackupJob job) return;
        selectionBeforeEdit = job;
        LoadJobEditor(job);
        editingId = null;
        var name = job.Name + " — копия";
        var suffix = 2;
        while (jobs.Any(existing => string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = job.Name + " — копия " + suffix++;
        JobName.Text = name;
        copiedSourceName = name;
        Enabled.IsChecked = false;
        OpenEditor();
        WizardHint.Text = "Настройки перенесены из «" + job.Name + "». Выберите другой источник: название подставится автоматически. Его можно изменить вручную. Автоматические запуски новой копии задания отключены.";
        JobName.SelectAll();
    }

    private void UpdateCopiedSourceName(string? baseName = null)
    {
        if (!editorOpen || copiedSourceName is null || JobName.Text != copiedSourceName
            || Kind.SelectedItem is not (DatabaseKind.OneCFile or DatabaseKind.FilesAndFolders)) return;
        try
        {
            var source = Source.Text.Trim();
            if (!Path.IsPathFullyQualified(source)) return;
            var path = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Kind.SelectedItem is DatabaseKind.OneCFile
                && Path.GetFileName(path).Equals("1Cv8.1CD", StringComparison.OrdinalIgnoreCase))
                path = Path.GetDirectoryName(path) ?? "";
            var name = string.IsNullOrWhiteSpace(baseName) ? Path.GetFileName(path) : baseName;
            if (string.IsNullOrWhiteSpace(name)) return;
            JobName.Text = copiedSourceName = name;
        }
        catch (ArgumentException) { /* Incomplete manually entered paths are not a name suggestion. */ }
    }

    private void Jobs_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Ignore headers and unused table space: only a row opens the editor.
        if (ItemsControl.ContainerFromElement(Jobs, e.OriginalSource as DependencyObject) is DataGridRow)
            EditJob_Click(sender, e);
    }

    private async void ToggleJobSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (!canSave || persisting || !JobScheduleButton.IsEnabled || Jobs.SelectedItem is not BackupJob selected) return;
        var job = jobs.FirstOrDefault(candidate => candidate.Id == selected.Id);
        if (job is null || job.ScheduleKind == ScheduleKind.Manual) return;
        var updated = job with { Enabled = !job.Enabled };
        if (!await PersistAsync(jobs.Select(candidate => candidate.Id == job.Id ? updated : candidate).ToList())) return;
        Jobs.SelectedItem = updated;
        Status.Text = updated.Enabled
            ? backgroundBusy || schedulingTask is not null
                ? "Расписание выбранного задания включено. Планировщик учтёт изменение при следующем обновлении заданий."
                : "Расписание выбранного задания включено. Для выполнения включите автоматическое копирование."
            : "Расписание выбранного задания выключено. Ручное копирование доступно; копии, уже принятые к выполнению, не отменяются.";
    }

    private async void CancelEditor_Click(object sender, RoutedEventArgs e)
    {
        if (persisting || !await ConfirmLeavingEditorAsync()) return;
        // Saving from the confirmation has already closed the editor.
        if (!editorOpen) return;
        CloseEditor();
        // Reload the saved model; draft fields are never used by a manual run.
        if (selectionBeforeEdit is not null) Jobs.SelectedItem = jobs.FirstOrDefault(job => job.Id == selectionBeforeEdit.Id);
        if (Jobs.SelectedItem is BackupJob saved) LoadJobEditor(saved);
        Status.Text = "Изменения не сохранены.";
    }

    private void PreviousStep_Click(object sender, RoutedEventArgs e)
    { if (wizardStep > 0 && !persisting) SetWizardStep(wizardStep - 1); }

    private void NextStep_Click(object sender, RoutedEventArgs e)
    {
        if (!editorOpen || persisting || !ValidateWizardStep(wizardStep)) return;
        SetWizardStep(Math.Min(3, wizardStep + 1));
    }

    private void SetWizardStep(int step, bool keepError = false)
    {
        wizardStep = step;
        BaseStep.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        StorageStep.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        ScheduleStep.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        ReviewStep.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        PreviousStepButton.IsEnabled = step > 0;
        NextStepButton.Visibility = step < 3 ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        JobEditor.Margin = step == 3 ? new Thickness(20, 8, 20, 8) : new Thickness(20, 16, 20, 16);
        WizardHint.Margin = new Thickness(0, 6, 0, step == 3 ? 8 : 16);
        var labels = new[] { "Источник", "Хранилище", "Расписание", "Проверка" };
        WizardProgress.Text = $"Шаг {step + 1} из 4 · " + string.Join("  →  ", labels.Select((label, index) => index == step ? "[" + label + "]" : label));
        (WizardHeading.Text, WizardHint.Text) = step switch
        {
            0 => ("Что будем копировать?", "Название поможет найти задание. Выберите источник и способ копирования."),
            1 => ("Куда сохранять копии?", "Выберите отдельное место хранения. При необходимости включите ZIP и защиту паролем."),
            2 => ("Когда запускать и сколько хранить?", "Сначала можно выбрать «Только вручную» и проверить копию. Расписание включается отдельно от фоновой работы агента."),
            _ => ("Всё верно?", "Проверьте итог. Полные пути и параметры можно раскрыть ниже.")
        };
        UpdateFileBackupControls();
        if (!keepError)
        {
            WizardError.Visibility = Visibility.Collapsed;
            Status.Text = "Настройка задания. Изменения ещё не сохранены.";
        }
        if (step is 2 or 3) UpdateSchedulePreview();
        if (step == 3)
        {
            var type = Kind.SelectedItem switch { DatabaseKind.SqlServer => "MS SQL", DatabaseKind.PostgreSql => "PostgreSQL", DatabaseKind.FilesAndFolders => "Файлы и папки", DatabaseKind.WebDav => "Файлы WebDAV", _ => "Файловая 1С" };
            var method = Kind.SelectedItem is DatabaseKind.OneCFile
                ? OneCMethodPicker.SelectedValue is OneCBackupMethod.FileArchive ? "ZIP файла базы, без платформы" : "Выгрузка .dt через платформу 1С"
                : Kind.SelectedItem is DatabaseKind.FilesAndFolders or DatabaseKind.WebDav
                    ? FileBackupModePicker.SelectedValue is FileBackupMode.Incremental ? "Инкрементная, первая копия полная" : "Полная файловая копия"
                    : Kind.SelectedItem is DatabaseKind.SqlServer ? "Полная копия .bak" : "Дамп .dump";
            var schedule = Schedule.SelectedValue is ScheduleKind.Manual ? "Только вручную"
                : Enabled.IsChecked != true ? "Автоматические запуски отключены"
                : Schedule.SelectedValue is ScheduleKind.SystemStartup ? "При запуске системы, после входа в Windows"
                : (Schedule.SelectedValue is ScheduleKind.Weekly ? "По дням: " + string.Join(", ", DayControls.Where(day => day.Box.IsChecked == true).Select(day => day.Box.Content)) : "Ежедневно") + " · " + DailyAt.Text.Trim();
            var format = EncryptArchive.IsChecked == true ? "ZIP с паролем (AES-256)"
                : ArchiveCopies.IsChecked == true || OneCMethodPicker.SelectedValue is OneCBackupMethod.FileArchive && Kind.SelectedItem is DatabaseKind.OneCFile
                    ? "ZIP без пароля" : "Исходный формат копии";
            var connection = Kind.SelectedItem is DatabaseKind.SqlServer ? "\nСервер: " + SqlBackupServer.Text.Trim()
                : Kind.SelectedItem is DatabaseKind.PostgreSql ? "\nСервер: " + PgHost.Text.Trim() + ":" + PgPort.Text.Trim() : "";
            var ftpAddress = Storage.SelectedValue is StorageKind.Ftp ? "\nFTP-сервер: " + FtpHost.Text.Trim() + ":" + FtpPort.Text.Trim()
                + (FtpTls.IsChecked == true ? " (FTPS)" : " (без TLS)") : "";
            var platformDescription = Kind.SelectedItem is DatabaseKind.OneCFile && OneCMethodPicker.SelectedValue is OneCBackupMethod.PlatformDump
                ? "\nПлатформа: " + (AutoOneCPlatform.IsChecked == true
                    ? string.IsNullOrWhiteSpace(OneCPlatformVersion.Text) ? "самая новая установленная" : "версия " + OneCPlatformVersion.Text.Trim()
                    : OneCExecutable.Text.Trim()) : "";
            var zoneDescription = "";
            var extraDestinations = additionalDestinations.Count == 0 ? "" : "\nТакже отправлять:\n" + string.Join("\n", additionalDestinations.Select(destination => "• " + destination.Description
                + (destination.KeepLast is null ? " (" + KeepLast.Text.Trim() + ")" : "")));
            var temporary = string.IsNullOrWhiteSpace(TemporaryFolder.Text)
                ? Path.Combine(DesktopPaths.DataDirectory, "work") + " (по умолчанию)" : TemporaryFolder.Text.Trim();
            WizardSummary.Text = $"{JobName.Text.Trim()}\nБаза: {type}\nИсточник: {Source.Text.Trim()}{connection}\nСпособ: {method}{platformDescription}\nХранилище: {(Storage.SelectedItem as KeyValuePair<StorageKind, string>?)?.Value}{ftpAddress}\nПапка: {(Storage.SelectedValue is StorageKind.GoogleDrive ? googleDriveSettings?.FolderName : Destination.Text.Trim())}{extraDestinations}\nВременная папка: {temporary}\nЗащита: {format}\nЗапуск: {schedule}{zoneDescription}\nОсновное хранилище: хранить {KeepLast.Text.Trim()} успешных копий";
            WizardSummary.Text += "\n\n" + NextRun.Text;
            if (Kind.SelectedItem is DatabaseKind.SqlServer)
                WizardSummary.Text += "\nВход в MS SQL: " + (SelectedSqlAuthentication == SqlServerAuthentication.Windows
                    ? "учётная запись Windows исполнителя" : "сохранённый логин MS SQL");
            if (Kind.SelectedItem is DatabaseKind.PostgreSql)
                WizardSummary.Text += "\nВход в PostgreSQL: " + (UseSavedPostgreSqlPassword ? "пароль, сохранённый в Windows" : "существующие настройки PostgreSQL исполнителя");
            if (Kind.SelectedItem is DatabaseKind.PostgreSql)
                WizardSummary.Text += "\nЗащита подключения: " + (PgTlsRequired.IsChecked == true ? "TLS с проверкой сертификата и имени сервера" : "прежние настройки PostgreSQL исполнителя");
            if (Kind.SelectedItem is DatabaseKind.FilesAndFolders or DatabaseKind.WebDav && FileBackupModePicker.SelectedValue is FileBackupMode.Incremental)
                WizardSummary.Text += "\n" + FullBackupPlanDescription + " Нужные предшествующие архивы сохраняются сверх лимита хранилища.";
            ReviewTechnicalDetails.IsExpanded = false;
            ReviewJobName.Text = JobName.Text.Trim();
            ReviewJobName.ToolTip = ReviewJobName.Text;
            var sourceLabel = Source.Text.Trim();
            if (Kind.SelectedItem is DatabaseKind.OneCFile or DatabaseKind.FilesAndFolders)
            {
                sourceLabel = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceLabel));
                if (string.IsNullOrEmpty(sourceLabel)) sourceLabel = Source.Text.Trim();
            }
            ReviewDatabase.Text = type + " · " + sourceLabel;
            ReviewDatabase.ToolTip = Source.Text.Trim() + connection;
            var shortMethod = Kind.SelectedItem switch
            {
                DatabaseKind.SqlServer => "SQL .bak", DatabaseKind.PostgreSql => "PG .dump",
                DatabaseKind.FilesAndFolders or DatabaseKind.WebDav => FileBackupModePicker.SelectedValue is FileBackupMode.Incremental ? "Инкрементная, первая полная" : "Полная",
                _ => OneCMethodPicker.SelectedValue is OneCBackupMethod.FileArchive ? "1Cv8.1CD, без платформы" : "Выгрузка .dt"
            };
            if (Kind.SelectedItem is DatabaseKind.OneCFile && OneCMethodPicker.SelectedValue is OneCBackupMethod.FileArchive)
            {
                shortMethod += AllowOpenOneCDatabase.IsChecked == true ? " · Открытая база разрешена — риск повреждения" : " · Только закрытая база";
                if (AllowOpenOneCDatabase.IsChecked == true) WizardSummary.Text += "\n\n" + OneCFileArchiveExecutor.OpenDatabaseWarning;
            }
            ReviewFormat.Text = format + " · " + shortMethod;
            ReviewFormat.ToolTip = ReviewFormat.Text + platformDescription;
            static string StorageLabel(StorageKind kind, bool tls) => kind switch
            { StorageKind.GoogleDrive => "Google Drive", StorageKind.RemoteGateway => "Шлюз", StorageKind.Ftp => tls ? "FTPS" : "FTP", StorageKind.NetworkShare => "Сеть", _ => "Диск" };
            var storageSummaries = new[] { StorageLabel((StorageKind)Storage.SelectedValue, FtpTls.IsChecked == true) + " — " + KeepLast.Text.Trim() }
                .Concat(additionalDestinations.Select(destination => StorageLabel(destination.StorageKind, destination.Ftp?.UseTls == true)
                    + " — " + (destination.KeepLast?.ToString(CultureInfo.InvariantCulture) ?? KeepLast.Text.Trim())));
            ReviewStorages.Text = string.Join("; ", storageSummaries) + " копий";
            ReviewStorages.ToolTip = (Storage.SelectedValue is StorageKind.GoogleDrive ? googleDriveSettings?.FolderName : Destination.Text.Trim()) + ftpAddress + extraDestinations;
            ReviewSchedule.Text = schedule;
            ReviewSchedule.ToolTip = schedule + zoneDescription + "\n" + NextRun.Text;
            ReviewTemporary.Text = temporary;
            ReviewTemporary.ToolTip = temporary;
        }
        // Realize the scroll content before focusing a field on a newly opened step.
        UpdateLayout();
        EditorScroll.ScrollToTop();
    }

    private bool RejectWizard(string message, int step, FrameworkElement field)
    {
        SetWizardStep(step, keepError: true);
        WizardError.Text = message;
        WizardError.Visibility = Visibility.Visible;
        Status.Text = "Настройки ещё не сохранены. " + message;
        for (DependencyObject? parent = field; parent is not null; parent = LogicalTreeHelper.GetParent(parent))
            if (parent is Expander expander) expander.IsExpanded = true;
        field.BringIntoView(); field.Focus();
        return false;
    }

    private bool ValidateWizardStep(int step)
    {
        if (step == 0)
        {
            if (string.IsNullOrWhiteSpace(JobName.Text)) return RejectWizard("Введите название, например «Бухгалтерия».", 0, JobName);
            if (string.IsNullOrWhiteSpace(Source.Text)) return RejectWizard(Kind.SelectedItem is DatabaseKind.FilesAndFolders
                ? "Выберите исходный файл или папку." : Kind.SelectedItem is DatabaseKind.WebDav ? "Укажите HTTPS-адрес папки WebDAV." : "Выберите папку базы 1С или введите имя базы SQL.", 0, Source);
            if (Kind.SelectedItem is DatabaseKind.FilesAndFolders)
            {
                if (!Path.IsPathFullyQualified(Source.Text.Trim())) return RejectWizard("Выберите файл или папку либо укажите полный путь.", 0, Source);
                if (FileBackupModePicker.SelectedValue is FileBackupMode.Incremental && SelectedFullBackupDay is null
                    && (!int.TryParse(FullBackupEvery.Text, out var every) || every is < 1 or > 365))
                    return RejectWizard("Укажите от 1 до 365 архивов в инкрементной цепочке.", 0, FullBackupEvery);
            }
            if (Kind.SelectedItem is DatabaseKind.WebDav)
            {
                var errors = WebDavSettings.ValidateSource(Source.Text.Trim());
                if (errors.Count != 0) return RejectWizard(string.Join(" ", errors), 0, Source);
                if (string.IsNullOrEmpty(webDavCredentialKey)) return RejectWizard("Нажмите «Логин и пароль…» и сохраните подключение WebDAV.", 0, WebDavCredentialsButton);
                if (!int.TryParse(WebDavTimeout.Text, out var timeout) || timeout is < 1 or > 1440)
                    return RejectWizard("Время ожидания WebDAV: от 1 до 1440 минут.", 0, WebDavTimeout);
                if (FileBackupModePicker.SelectedValue is FileBackupMode.Incremental && SelectedFullBackupDay is null
                    && (!int.TryParse(FullBackupEvery.Text, out var every) || every is < 1 or > 365))
                    return RejectWizard("Укажите от 1 до 365 архивов в цепочке.", 0, FullBackupEvery);
            }
            if (Kind.SelectedItem is DatabaseKind.OneCFile && OneCMethodPicker.SelectedValue is OneCBackupMethod.PlatformDump)
            {
                if (AutoOneCPlatform.IsChecked != true && string.IsNullOrWhiteSpace(OneCExecutable.Text)) return RejectWizard("Для выгрузки .dt выберите программу 1С (1cv8.exe). Для ZIP файла базы платформа не нужна.", 0, OneCExecutable);
                if (!int.TryParse(OneCTimeout.Text, out var timeout)) return RejectWizard("Время ожидания должно быть целым числом минут.", 0, OneCTimeout);
                var errors = ReadOneCSettings(timeout).Validate();
                if (errors.Count != 0) return RejectWizard(string.Join(" ", errors), 0, OneCExecutable);
            }
            if (Kind.SelectedItem is DatabaseKind.OneCFile && !Path.IsPathFullyQualified(Source.Text.Trim()))
                return RejectWizard("Укажите полный путь к базе, например D:\\Bases\\Accounting, или выберите папку кнопкой справа.", 0, Source);
            if (Kind.SelectedItem is DatabaseKind.SqlServer)
            {
                if (!int.TryParse(SqlBackupTimeout.Text, out var timeout)) return RejectWizard("Время ожидания должно быть целым числом минут.", 0, SqlBackupTimeout);
                if (SelectedSqlAuthentication == SqlServerAuthentication.SqlServer && string.IsNullOrWhiteSpace(sqlCredentialKey))
                    return RejectWizard("Нажмите «Задать логин и пароль MS SQL…» и сохраните данные входа.", 0, SqlCredentialsButton);
                var errors = ReadSqlSettings(timeout).Validate();
                if (errors.Count != 0) return RejectWizard(string.Join(" ", errors), 0, SqlBackupServer);
            }
            if (Kind.SelectedItem is DatabaseKind.PostgreSql)
            {
                if (UseSavedPostgreSqlPassword && postgreSqlCredentialKey is null)
                    return RejectWizard("Нажмите «Задать пароль PostgreSQL…» и сохраните пароль выбранного пользователя.", 0, PgCredentialsButton);
                if (string.IsNullOrWhiteSpace(PgDumpPath.Text) || string.IsNullOrWhiteSpace(PgRestorePath.Text))
                    return RejectWizard("Нажмите «Найти PostgreSQL на этом компьютере» или выберите pg_dump и pg_restore вручную.", 0, FindPostgreSqlToolsButton);
                if (!Path.IsPathFullyQualified(PgDumpPath.Text.Trim()))
                    return RejectWizard("Выберите установленный pg_dump.exe или укажите полный путь к нему.", 0, PgDumpPath);
                if (!Path.IsPathFullyQualified(PgRestorePath.Text.Trim()))
                    return RejectWizard("Выберите установленный pg_restore.exe или укажите полный путь к нему.", 0, PgRestorePath);
                if (!int.TryParse(PgPort.Text, out var port)) return RejectWizard("Порт PostgreSQL должен быть числом от 1 до 65535.", 0, PgPort);
                if (!int.TryParse(PgTimeout.Text, out var timeout)) return RejectWizard("Время ожидания должно быть целым числом минут.", 0, PgTimeout);
                var settings = ReadPostgreSqlSettings(port, timeout);
                var errors = (new BackupJob { Name = JobName.Text.Trim(), Source = Source.Text.Trim(), Kind = DatabaseKind.PostgreSql,
                    DestinationDirectory = Path.GetTempPath(), PostgreSql = settings }).Validate();
                if (errors.Count != 0) return RejectWizard(string.Join(" ", errors), 0, PgHost);
            }
        }
        if (step == 1)
        {
            var temporary = string.IsNullOrWhiteSpace(TemporaryFolder.Text) ? null : TemporaryFolder.Text.Trim();
            if (temporary is not null && (!Path.IsPathFullyQualified(temporary) || temporary.Any(char.IsControl)))
                return RejectWizard("Укажите полный путь к временной папке или выберите её кнопкой справа.", 1, TemporaryFolder);
            if (string.IsNullOrWhiteSpace(Destination.Text)) return RejectWizard("Выберите папку, в которой будут храниться резервные копии.", 1, Destination);
            if (Storage.SelectedValue is StorageKind.LocalDirectory or StorageKind.NetworkShare && !Path.IsPathFullyQualified(Destination.Text.Trim()))
                return RejectWizard("Укажите полный путь к папке копий или выберите её кнопкой справа.", 1, Destination);
            var ftpPort = 21;
            if (Storage.SelectedValue is StorageKind.Ftp && !int.TryParse(FtpPort.Text, out ftpPort)) return RejectWizard("Порт FTP должен быть числом от 1 до 65535.", 1, FtpPort);
            if (Storage.SelectedValue is StorageKind.Ftp)
            {
                if (string.IsNullOrWhiteSpace(FtpHost.Text)) return RejectWizard("Введите адрес FTP-сервера, например ftp.example.com.", 1, FtpHost);
                if (ftpPort is < 1 or > 65535) return RejectWizard("Порт FTP должен быть от 1 до 65535.", 1, FtpPort);
                if (string.IsNullOrWhiteSpace(FtpCredentialKey.Text)) return RejectWizard("Нажмите «Настроить логин и пароль», чтобы сохранить доступ к FTP.", 1, FtpCredentialsButton);
            }
            var job = new BackupJob { Name = "check", Source = "check", DestinationDirectory = Destination.Text.Trim(), StorageKind = (StorageKind)Storage.SelectedValue,
                TemporaryDirectory = temporary,
                GoogleDrive = Storage.SelectedValue is StorageKind.GoogleDrive ? googleDriveSettings : null,
                RemoteGateway = Storage.SelectedValue is StorageKind.RemoteGateway ? remoteGatewaySettings : null,
                ArchiveCopies = EncryptArchive.IsChecked == true, ArchivePasswordKey = EncryptArchive.IsChecked == true ? archivePasswordKey : null,
                AdditionalDestinations = additionalDestinations.Count == 0 ? null : additionalDestinations.ToArray(),
                Ftp = Storage.SelectedValue is StorageKind.Ftp ? new FtpSettings { Host = FtpHost.Text.Trim(), Port = ftpPort, UseTls = FtpTls.IsChecked == true,
                    CredentialKey = FtpCredentialKey.Text.Trim() } : null };
            var errors = job.Validate();
            if (errors.Count != 0) return RejectWizard(string.Join(" ", errors), 1, Destination);
            if (EncryptArchive.IsChecked == true && archivePasswordKey is null) return RejectWizard("Нажмите «Задать новый пароль архива», затем введите пароль дважды.", 1, ArchivePasswordButton);
            if (EncryptArchive.IsChecked == true && ArchiveCopies.IsChecked != true && !(Kind.SelectedItem is DatabaseKind.OneCFile && OneCMethodPicker.SelectedValue is OneCBackupMethod.FileArchive))
                return RejectWizard("Для защиты паролем включите архивацию в ZIP.", 1, ArchiveCopies);
        }
        if (step == 2)
        {
            if (!int.TryParse(KeepLast.Text, out var keep) || keep < 1) return RejectWizard("Укажите число хранимых копий: минимум 1.", 2, KeepLast);
            TimeOnly[] times = [new(23, 0)];
            if (Schedule.SelectedValue is ScheduleKind.Daily or ScheduleKind.Weekly && !TryParseRunTimes(DailyAt.Text, out times)) return RejectWizard("Введите время в формате 23:00. Несколько запусков: 09:00, 13:00, 23:00, без повторов.", 2, DailyAt);
            var job = new BackupJob { ScheduleKind = (ScheduleKind)Schedule.SelectedValue, DaysOfWeek = SelectedDays(), TimeZoneId = BackupSchedule.LocalTimeZone,
                DailyAt = times[0], AdditionalDailyTimes = times.Skip(1).ToArray() };
            var errors = job.ValidateSchedule();
            if (errors.Count != 0) return RejectWizard(string.Join(" ", errors), 2, DailyAt);
        }
        return true;
    }


    private void UpdateSelectedJobSummary()
    {
        if (SelectedJobSummary is null) return;
        JobProgress.Visibility = Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetName(JobProgress, checkingLatestCopies ? "Проверка последних копий" : checkingStorages ? "Проверка хранилищ" : "Создание резервной копии");
        if (checkingStorages)
        {
            SelectedJobSummary.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
            JobProgress.Visibility = Visibility.Visible;
            SelectedJobSummary.Text = checkingLatestCopies
                ? "Проверяются последние копии во всех хранилищах. База не копируется; дождитесь результата или отмените проверку."
                : "Проверяются временная папка и хранилища. Дождитесь результата или отмените проверку.";
            return;
        }
        SelectedJobSummary.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
        if (Jobs.SelectedItem is not BackupJob job) { SelectedJobSummary.Text = "Выберите задание, чтобы создать копию или изменить настройки."; return; }
        var latest = LatestJobRuns.Runs?.GetValueOrDefault(job.Id);
        var running = activeJobId == job.Id || latest?.Status == BackupRunStatus.Running;
        JobProgress.Visibility = running && BackupProgressPanel.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        DateTimeOffset? next;
        try { next = BackupSchedule.NextRun(job, DateTimeOffset.UtcNow); }
        catch (ArgumentException) { SelectedJobSummary.Text = "Проверьте расписание задания."; return; }
        catch (InvalidOperationException) { SelectedJobSummary.Text = "Проверьте расписание задания."; return; }
        var nextText = job.Enabled && job.ScheduleKind == ScheduleKind.SystemStartup ? "При загрузке системы: одна попытка после запуска агента"
            : !job.Enabled || job.ScheduleKind == ScheduleKind.Manual || next is null ? "Автозапуск для задания выключен"
            : "По расписанию: " + next.Value.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture)
                + (backgroundBusy && backgroundState?.Phase == LocalAgentPhase.Running || schedulingTask is not null ? "" : " · включите автоматическое копирование");
        var resultText = running ? "Создаётся копия. Дождитесь результата; отмена доступна в программе, которая запустила копирование."
            : LatestJobRuns.IsLoading ? "Загружается история запусков."
            : LatestJobRuns.Runs is null ? "История запусков недоступна. Откройте «История запусков» и нажмите «Обновить»."
            : latest is null ? "Первая копия ещё не создана. Нажмите «Создать копию», чтобы проверить настройки."
            : latest.Status switch
            {
                BackupRunStatus.Succeeded when latest.Warnings?.Count > 0 => "Копия создана, есть предупреждения. Откройте «Результат».",
                BackupRunStatus.Succeeded when latest.Destinations is not null => $"Копия доставлена во все {latest.Destinations.Count} хранилища. Откройте «Результат».",
                BackupRunStatus.Succeeded => "Копия создана. Откройте «Последняя копия», чтобы проверить её или найти файл.",
                BackupRunStatus.PartiallySucceeded => $"Копия доставлена в {latest.Destinations?.Count(destination => destination.Record is not null)} из {latest.Destinations?.Count} хранилищ. Откройте «Результат» для ошибок доставки.",
                BackupRunStatus.Cancelled => "Последний запуск отменён. Откройте «Результат» перед повторным запуском.",
                BackupRunStatus.Interrupted => "Последний запуск прерван. Откройте «Результат» перед повторным запуском.",
                _ => "Копирование завершилось с ошибкой. Откройте «Результат», чтобы узнать причину."
            };
        if (!running && latest is not null)
        {
            resultText += " · " + (latest.FinishedUtc ?? latest.StartedUtc).ToLocalTime().ToString("dd.MM HH:mm");
            var brush = latest.Status == BackupRunStatus.Succeeded
                ? latest.Warnings?.Count > 0 ? "WarningBrush" : "SuccessBrush" : "ErrorBrush";
            SelectedJobSummary.Foreground = (System.Windows.Media.Brush)FindResource(brush);
        }
        var filePlan = !job.IsFileSnapshot ? "" : job.FileBackupMode == FileBackupMode.Full ? "Полная при каждом запуске"
            : job.FullBackupDayOfWeek is { } day
                ? "Инкрементная · Полная: " + CultureInfo.GetCultureInfo("ru-RU").DateTimeFormat.GetDayName(day)
                    + "; при пропуске — при ближайшем запуске"
                : $"Инкрементная · до {job.FullBackupEvery} архивов в цепочке";
        SelectedJobSummary.Text = resultText + "\n" + nextText + (filePlan.Length == 0 ? "" : "\n" + filePlan);
    }

    private async void ShowLatestCopy_Click(object sender, RoutedEventArgs e)
    {
        if (!ready || copiesRefreshing || Jobs.SelectedItem is not BackupJob job) return;
        try
        {
            ShowPage("copies");
            await RefreshCopiesAsync();
            SelectCopiesForJob(job.Id);
            var latest = Copies.Items.OfType<BackupCopyRow>().FirstOrDefault(row => row.Record.JobId == job.Id);
            Copies.SelectedItem = latest;
            if (latest is not null)
            {
                Copies.ScrollIntoView(latest);
                CopyVerificationStatus.Text = "Выбрана последняя копия задания. Для проверки файла нажмите «Проверить целостность».";
            }
            else CopyVerificationStatus.Text = "Для этого задания нет зарегистрированных копий. Создайте копию в разделе «Задания».";
        }
        catch (Exception ex) { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex); Status.Text = "Не удалось открыть копию: " + ex.Message; }
    }

    private async void ShowLatestRun_Click(object sender, RoutedEventArgs e)
    {
        if (!ready || historyRefreshing || Jobs.SelectedItem is not BackupJob job) return;
        try
        {
            ShowPage("history");
            HistorySearch.Clear();
            HistoryStatusFilter.SelectedIndex = 0;
            await RefreshHistoryAsync();
            var latest = historyRuns.FirstOrDefault(run => run.JobId == job.Id);
            RunHistory.SelectedItem = latest;
            if (latest is not null) RunHistory.ScrollIntoView(latest);
        }
        catch (Exception ex) { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex); Status.Text = "Не удалось открыть результат: " + ex.Message; }
    }
}

public sealed class LocalTimeConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is DateTimeOffset time ? time.ToLocalTime().ToString("dd.MM.yyyy HH:mm", culture) : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
