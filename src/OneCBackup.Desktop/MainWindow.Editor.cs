using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

public partial class MainWindow
{
    private int wizardStep;
    private bool editorOpen;
    private BackupJob? selectionBeforeEdit;

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
        UpdateRunControls();
        JobName.Focus();
    }

    private void CloseEditor()
    {
        editorOpen = false;
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

    private void Jobs_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Ignore headers and unused table space: only a row opens the editor.
        if (ItemsControl.ContainerFromElement(Jobs, e.OriginalSource as DependencyObject) is DataGridRow)
            EditJob_Click(sender, e);
    }

    private void CancelEditor_Click(object sender, RoutedEventArgs e)
    {
        if (persisting) return;
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
        var labels = new[] { "База", "Хранилище", "Расписание", "Проверка" };
        WizardProgress.Text = $"Шаг {step + 1} из 4 · " + string.Join("  →  ", labels.Select((label, index) => index == step ? "[" + label + "]" : label));
        (WizardHeading.Text, WizardHint.Text) = step switch
        {
            0 => ("Что будем копировать?", "Название поможет найти задание. Для файловой 1С выберите папку базы или файл 1Cv8.1CD."),
            1 => ("Куда сохранять копии?", "Выберите отдельное место хранения. При необходимости включите ZIP и защиту паролем."),
            2 => ("Когда запускать и сколько хранить?", "Время указано в выбранном часовом поясе. Сохраняются только успешные копии."),
            _ => ("Всё верно?", "Проверьте настройки перед сохранением. Доступ к базе и хранилищу проверяется при создании первой копии.")
        };
        if (!keepError) WizardError.Visibility = Visibility.Collapsed;
        if (step == 3)
        {
            var type = Kind.SelectedItem switch { DatabaseKind.SqlServer => "Microsoft SQL Server", DatabaseKind.PostgreSql => "PostgreSQL", _ => "Файловая 1С" };
            var method = Kind.SelectedItem is DatabaseKind.OneCFile
                ? OneCMethodPicker.SelectedValue is OneCBackupMethod.FileArchive ? "ZIP файла базы, без платформы" : "Выгрузка .dt через платформу 1С"
                : Kind.SelectedItem is DatabaseKind.SqlServer ? "Полная копия .bak" : "Дамп .dump";
            var schedule = Enabled.IsChecked != true ? "Автоматические запуски отключены"
                : Schedule.SelectedValue is ScheduleKind.Manual ? "Только вручную"
                : (Schedule.SelectedValue is ScheduleKind.Weekly ? "По дням: " + string.Join(", ", DayControls.Where(day => day.Box.IsChecked == true).Select(day => day.Box.Content)) : "Ежедневно") + " · " + DailyAt.Text.Trim();
            var format = EncryptArchive.IsChecked == true ? "ZIP с паролем (AES-256)"
                : ArchiveCopies.IsChecked == true || OneCMethodPicker.SelectedValue is OneCBackupMethod.FileArchive && Kind.SelectedItem is DatabaseKind.OneCFile
                    ? "ZIP без пароля" : "Исходный формат копии";
            var connection = Kind.SelectedItem is DatabaseKind.SqlServer ? "\nСервер: " + SqlBackupServer.Text.Trim()
                : Kind.SelectedItem is DatabaseKind.PostgreSql ? "\nСервер: " + PgHost.Text.Trim() + ":" + PgPort.Text.Trim() : "";
            var ftpAddress = Storage.SelectedValue is StorageKind.Ftp ? "\nFTP-сервер: " + FtpHost.Text.Trim() + ":" + FtpPort.Text.Trim()
                + (FtpTls.IsChecked == true ? " (FTPS)" : " (без TLS)") : "";
            WizardSummary.Text = $"{JobName.Text.Trim()}\n\nБаза: {type}\nИсточник: {Source.Text.Trim()}{connection}\nСпособ: {method}\n\nХранилище: {(Storage.SelectedItem as KeyValuePair<StorageKind, string>?)?.Value}{ftpAddress}\nПапка: {Destination.Text.Trim()}\nЗащита: {format}\n\nЗапуск: {schedule}\nЧасовой пояс: {TimeZonePicker.Text}\nХранить: {KeepLast.Text.Trim()} успешных копий";
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
            if (string.IsNullOrWhiteSpace(Source.Text)) return RejectWizard("Выберите папку базы 1С или введите имя базы SQL.", 0, Source);
            if (Kind.SelectedItem is DatabaseKind.OneCFile && OneCMethodPicker.SelectedValue is OneCBackupMethod.PlatformDump)
            {
                if (string.IsNullOrWhiteSpace(OneCExecutable.Text)) return RejectWizard("Для выгрузки .dt выберите программу 1С (1cv8.exe). Для ZIP файла базы платформа не нужна.", 0, OneCExecutable);
                if (!int.TryParse(OneCTimeout.Text, out var timeout)) return RejectWizard("Время ожидания должно быть целым числом минут.", 0, OneCTimeout);
                var errors = new OneCSettings { ExecutablePath = OneCExecutable.Text.Trim(), TimeoutMinutes = timeout }.Validate();
                if (errors.Count != 0) return RejectWizard(string.Join(" ", errors), 0, OneCExecutable);
            }
            if (Kind.SelectedItem is DatabaseKind.OneCFile && !Path.IsPathFullyQualified(Source.Text.Trim()))
                return RejectWizard("Укажите полный путь к базе, например D:\\Bases\\Accounting, или выберите папку кнопкой справа.", 0, Source);
            if (Kind.SelectedItem is DatabaseKind.SqlServer)
            {
                if (!int.TryParse(SqlBackupTimeout.Text, out var timeout)) return RejectWizard("Время ожидания должно быть целым числом минут.", 0, SqlBackupTimeout);
                var errors = new SqlServerBackupSettings { Server = SqlBackupServer.Text.Trim(), ServerBackupDirectory = SqlServerDirectory.Text.Trim(),
                    AgentReadDirectory = SqlAgentDirectory.Text.Trim(), TimeoutMinutes = timeout }.Validate();
                if (errors.Count != 0) return RejectWizard(string.Join(" ", errors), 0, SqlBackupServer);
            }
            if (Kind.SelectedItem is DatabaseKind.PostgreSql)
            {
                if (!int.TryParse(PgPort.Text, out var port)) return RejectWizard("Порт PostgreSQL должен быть числом от 1 до 65535.", 0, PgPort);
                if (!int.TryParse(PgTimeout.Text, out var timeout)) return RejectWizard("Время ожидания должно быть целым числом минут.", 0, PgTimeout);
                var settings = new PostgreSqlBackupSettings { Host = PgHost.Text.Trim(), Port = port, Username = PgUsername.Text.Trim(),
                    PgDumpPath = PgDumpPath.Text.Trim(), PgRestorePath = PgRestorePath.Text.Trim(), TimeoutMinutes = timeout };
                var errors = (new BackupJob { Name = JobName.Text.Trim(), Source = Source.Text.Trim(), Kind = DatabaseKind.PostgreSql,
                    DestinationDirectory = Path.GetTempPath(), PostgreSql = settings }).Validate();
                if (errors.Count != 0) return RejectWizard(string.Join(" ", errors), 0, PgHost);
            }
        }
        if (step == 1)
        {
            if (string.IsNullOrWhiteSpace(Destination.Text)) return RejectWizard("Выберите папку, в которой будут храниться резервные копии.", 1, Destination);
            if (Storage.SelectedValue is not StorageKind.Ftp && !Path.IsPathFullyQualified(Destination.Text.Trim()))
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
            if (Schedule.SelectedValue is not ScheduleKind.Manual && !TryParseRunTimes(DailyAt.Text, out times)) return RejectWizard("Введите время в формате 23:00. Несколько запусков: 09:00, 13:00, 23:00, без повторов.", 2, DailyAt);
            var job = new BackupJob { ScheduleKind = (ScheduleKind)Schedule.SelectedValue, DaysOfWeek = SelectedDays(), TimeZoneId = Zone.Text.Trim(),
                DailyAt = times[0], AdditionalDailyTimes = times.Skip(1).ToArray() };
            var errors = job.ValidateSchedule();
            if (errors.Count != 0) return RejectWizard(string.Join(" ", errors), 2, TimeZonePicker);
        }
        return true;
    }

    private void TimeZone_Changed(object sender, SelectionChangedEventArgs e)
    { if (Zone is not null && TimeZonePicker.SelectedValue is string id && Zone.Text != id) Zone.Text = id; }

    private void UpdateSelectedJobSummary()
    {
        if (SelectedJobSummary is null) return;
        if (Jobs.SelectedItem is not BackupJob job) { SelectedJobSummary.Text = "Выберите задание, чтобы создать копию или изменить настройки."; return; }
        var latest = historyRuns.FirstOrDefault(run => run.JobId == job.Id);
        DateTimeOffset? next;
        try { next = BackupSchedule.NextRun(job, DateTimeOffset.UtcNow); }
        catch (ArgumentException) { SelectedJobSummary.Text = "Проверьте расписание и часовой пояс задания."; return; }
        catch (InvalidOperationException) { SelectedJobSummary.Text = "Проверьте расписание задания."; return; }
        var nextText = !job.Enabled || job.ScheduleKind == ScheduleKind.Manual || next is null ? "Автозапуск для задания выключен"
            : "По расписанию: " + TimeZoneInfo.ConvertTime(next.Value, TimeZoneInfo.FindSystemTimeZoneById(job.TimeZoneId)).ToString("dd.MM HH:mm", CultureInfo.InvariantCulture)
                + (backgroundBusy && backgroundState?.Phase == LocalAgentPhase.Running || schedulingTask is not null ? "" : " · включите автоматическое копирование");
        SelectedJobSummary.Text = (latest is null ? "Первая копия ещё не создана" : "Последний запуск: " + latest.StatusDescription + " · " + latest.StartedUtc.ToLocalTime().ToString("dd.MM HH:mm"))
            + "\n" + nextText;
    }
}

public sealed class LocalTimeConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is DateTimeOffset time ? time.ToLocalTime().ToString("dd.MM.yyyy HH:mm", culture) : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
