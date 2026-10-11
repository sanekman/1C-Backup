using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

/// <summary>Opt-in Windows CI check, isolated from the user's real profile and databases.</summary>
internal static class DesktopSmoke
{
    public static void Finish(string output, bool success, string details)
    {
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "smoke-result.json"), JsonSerializer.Serialize(new { success, details }));
        Application.Current.Shutdown(success ? 0 : 1);
    }

    private static async Task WaitUntil(Func<bool> predicate, string operation)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException(operation);
            await Task.Delay(100);
        }
    }

    public static async Task RunAsync(string output)
    {
        string? archiveCredentialKey = null;
        try
        {
            await WaitUntil(() => Application.Current.MainWindow is MainWindow { SmokeReady: true }, "Окно не завершило загрузку.");
            var window = (MainWindow)Application.Current.MainWindow;
            T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
            void Click(string name)
            {
                window.UpdateLayout();
                var button = Control<Button>(name);
                if (!button.IsEnabled || !button.IsVisible) throw new InvalidOperationException("Кнопка недоступна: " + name);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
            }
            void Screenshot(string name, Window? target = null)
            {
                target ??= window;
                target.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(target.ActualWidth), (int)Math.Ceiling(target.ActualHeight),
                    96, 96, PixelFormats.Pbgra32);
                bitmap.Render(target);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
            }
            void SetAdditionalStorage(string directory, bool edit = false, bool checkRequired = false, int? keepLast = null)
            {
                Control<Expander>("AdditionalStorageOptions").IsExpanded = true;
                window.UpdateLayout();
                Control<Button>(edit ? "EditStorageButton" : "AddStorageButton").BringIntoView();
                Exception? dialogFailure = null;
                _ = window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var dialog = Application.Current.Windows.OfType<DestinationWindow>().Single();
                    try
                    {
                        var save = (Button)dialog.FindName("SaveButton");
                        if (checkRequired)
                        {
                            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            if (((TextBlock)dialog.FindName("Status")).Text.Length == 0)
                                throw new InvalidOperationException("Пустое дополнительное хранилище принято.");
                        }
                        ((TextBox)dialog.FindName("Destination")).Text = directory;
                        var count = (TextBox)dialog.FindName("KeepLast");
                        if (checkRequired)
                        {
                            foreach (var invalid in new[] { "0", "-1", "not-a-number" })
                            {
                                count.Text = invalid;
                                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                if (!dialog.IsVisible || !((TextBlock)dialog.FindName("Status")).Text.Contains("минимум 1"))
                                    throw new InvalidOperationException("Некорректное количество копий принято.");
                            }
                        }
                        count.Text = keepLast?.ToString() ?? "";
                        count.BringIntoView();
                        Screenshot("additional-storage-dialog", dialog);
                        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    catch (Exception ex) { dialogFailure = ex; dialog.Close(); }
                }));
                Click(edit ? "EditStorageButton" : "AddStorageButton");
                if (dialogFailure is not null) throw dialogFailure;
            }
            void AssertVisibleFooter(string buttonName)
            {
                window.UpdateLayout();
                var button = Control<Button>(buttonName);
                var position = button.TranslatePoint(new Point(), window);
                if (!button.IsVisible || position.Y < 0 || position.Y + button.ActualHeight > window.ActualHeight - 20)
                    throw new InvalidOperationException("Кнопка мастера выходит за границы окна: " + buttonName);
            }
            Screenshot("first-start");
            var expectedVersion = Environment.GetEnvironmentVariable("GITHUB_REF_TYPE") == "tag"
                ? Environment.GetEnvironmentVariable("GITHUB_REF_NAME")![1..] : "0.1.0-dev";
            if (ApplicationBuildInfo.Version != expectedVersion || Control<TextBlock>("AppVersion").Text != expectedVersion)
                throw new InvalidOperationException("Версия программы не соответствует сборке: " + ApplicationBuildInfo.Version);
            var expectedRevision = Environment.GetEnvironmentVariable("GITHUB_SHA");
            if (expectedRevision is not null && ApplicationBuildInfo.Revision != expectedRevision)
                throw new InvalidOperationException("Идентификатор сборки не соответствует исходному коду.");
            Click("AgentNavigation");
            Click("CopyBuildInfoButton");
            if (Clipboard.GetText() != ApplicationBuildInfo.SupportText || Control<TextBox>("BuildInfo").Text != Clipboard.GetText()
                || !Control<TextBlock>("BuildInfoStatus").Text.Contains("скопированы"))
                throw new InvalidOperationException("Сведения о программе не скопированы полностью.");
            File.WriteAllText(Path.Combine(output, "build-info.json"), JsonSerializer.Serialize(new
            { version = ApplicationBuildInfo.Version, revision = ApplicationBuildInfo.Revision, supportText = Clipboard.GetText() }));
            Screenshot("about-program");
            Click("JobsNavigation");
            Click("FirstJobButton");
            Click("NextStepButton");
            if (!Control<TextBlock>("WizardError").IsVisible || !Control<StackPanel>("BaseStep").IsVisible)
                throw new InvalidOperationException("Пустое задание не остановлено на первом шаге.");
            if (Control<ComboBox>("OneCMethodPicker").SelectedValue is not OneCBackupMethod.FileArchive)
                throw new InvalidOperationException("Первое задание не предлагает копию файла без платформы.");
            var source = Path.Combine(DesktopPaths.DataDirectory, "fixture-base");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "1Cv8.1CD"), "CI fixture bytes; not a real 1C database");
            Control<TextBox>("JobName").Text = "Тестовая база 1С";
            Control<TextBox>("Source").Text = source;
            Control<ComboBox>("OneCMethodPicker").SelectedValue = OneCBackupMethod.FileArchive;
            if (Control<TextBlock>("WizardError").IsVisible)
                throw new InvalidOperationException("Устаревшая ошибка осталась после исправления поля.");
            Screenshot("wizard-base");
            Click("NextStepButton");
            Click("NextStepButton");
            if (!Control<TextBlock>("WizardError").IsVisible || !Control<StackPanel>("StorageStep").IsVisible)
                throw new InvalidOperationException("Пустая папка копий не остановила мастер.");
            Control<TextBox>("Destination").Text = Path.Combine(DesktopPaths.DataDirectory, "copies");
            Control<TextBox>("TemporaryFolder").Text = "relative-work-folder";
            Click("NextStepButton");
            if (!Control<TextBlock>("WizardError").Text.Contains("временной папке") || !Control<StackPanel>("StorageStep").IsVisible)
                throw new InvalidOperationException("Относительная временная папка не остановила мастер.");
            var temporaryDirectory = Path.Combine(DesktopPaths.DataDirectory, "HDD temporary");
            Directory.CreateDirectory(temporaryDirectory);
            var temporaryMarker = Path.Combine(temporaryDirectory, "unrelated.txt");
            File.WriteAllText(temporaryMarker, "keep unrelated files");
            Control<TextBox>("TemporaryFolder").Text = temporaryDirectory;
            Control<TextBox>("TemporaryFolder").BringIntoView();
            Screenshot("wizard-temporary-folder");
            var secondaryDirectory = Path.Combine(DesktopPaths.DataDirectory, "second-copies");
            SetAdditionalStorage(secondaryDirectory, checkRequired: true, keepLast: 2);
            Control<ListBox>("AdditionalStorageList").SelectedIndex = 0;
            SetAdditionalStorage(Control<TextBox>("Destination").Text, edit: true, keepLast: 2);
            Click("NextStepButton");
            if (!Control<TextBlock>("WizardError").Text.Contains("несколько раз") || !Control<StackPanel>("StorageStep").IsVisible)
                throw new InvalidOperationException("Повторное хранилище не остановило мастер.");
            SetAdditionalStorage(secondaryDirectory, edit: true, keepLast: 2);
            Screenshot("wizard-multiple-storage");
            Control<Expander>("ArchiveOptions").IsExpanded = true;
            Control<Button>("ArchivePasswordButton").BringIntoView();
            _ = window.Dispatcher.BeginInvoke(new Action(() =>
            {
                var passwordDialog = Application.Current.Windows.OfType<ArchivePasswordWindow>().Single();
                ((PasswordBox)passwordDialog.FindName("Password")).Password = "CI-only-архив-🗝-2026";
                ((PasswordBox)passwordDialog.FindName("Confirmation")).Password = "CI-only-архив-🗝-2026";
                ((Button)passwordDialog.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                archiveCredentialKey = passwordDialog.NewKey;
            }));
            Click("ArchivePasswordButton");
            if (Control<CheckBox>("EncryptArchive").IsChecked != true)
                throw new InvalidOperationException("Пароль архива не настроен через интерфейс.");
            Screenshot("wizard-storage");
            Click("NextStepButton");
            var previewBefore = DateTimeOffset.UtcNow;
            Control<TextBox>("DailyAt").Text = "23:00, 09:00, 13:00";
            var originalZone = Control<TextBox>("Zone").Text;
            void CheckUpcoming(TimeSpan offset, DayOfWeek? day, TimeOnly[] times, DateTimeOffset before)
            {
                var lines = Control<TextBlock>("NextRun").Text.Split('\n');
                if (lines.Length != 4 || !lines[0].StartsWith("Ближайшие 3 запуска") || !Control<TextBlock>("SchedulePreviewHint").IsVisible)
                    throw new InvalidOperationException("Три ближайших запуска не показаны.");
                var actual = lines.Skip(1).Select(line => DateTimeOffset.ParseExact(line, "dd.MM.yyyy HH:mm zzz", CultureInfo.InvariantCulture)).ToArray();
                DateTimeOffset[] Expected(DateTimeOffset instant)
                {
                    var date = instant.ToOffset(offset).Date;
                    return Enumerable.Range(0, 22).Select(index => date.AddDays(index))
                        .Where(value => day is null || value.DayOfWeek == day)
                        .SelectMany(value => times.Order().Select(time => new DateTimeOffset(value.Add(time.ToTimeSpan()), offset)))
                        .Where(value => value > instant).Take(3).ToArray();
                }
                if (actual.Any(value => value.Offset != offset) || (!actual.SequenceEqual(Expected(before)) && !actual.SequenceEqual(Expected(DateTimeOffset.UtcNow))))
                    throw new InvalidOperationException("Предпросмотр не соответствует времени, дням или часовому поясу задания.");
            }
            Control<TextBox>("Zone").Text = "UTC";
            CheckUpcoming(TimeSpan.Zero, null, [new(9, 0), new(13, 0), new(23, 0)], previewBefore);
            Control<ComboBox>("Schedule").SelectedValue = ScheduleKind.Weekly;
            foreach (var name in new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" }) Control<CheckBox>(name).IsChecked = false;
            if (Control<TextBlock>("NextRun").Text.StartsWith("Ближайшие") || Control<TextBlock>("SchedulePreviewHint").IsVisible)
                throw new InvalidOperationException("Недельное расписание без дней показывает устаревшие даты.");
            Click("NextStepButton");
            if (!Control<TextBlock>("WizardError").IsVisible || !Control<StackPanel>("ScheduleStep").IsVisible)
                throw new InvalidOperationException("Недельное расписание без дней принято.");
            Control<TextBox>("DailyAt").Text = "09:00";
            previewBefore = DateTimeOffset.UtcNow;
            Control<CheckBox>("Fri").IsChecked = true;
            CheckUpcoming(TimeSpan.Zero, DayOfWeek.Friday, [new(9, 0)], previewBefore);
            previewBefore = DateTimeOffset.UtcNow;
            Control<ComboBox>("TimeZonePicker").SelectedValue = "Russian Standard Time";
            CheckUpcoming(TimeSpan.FromHours(3), DayOfWeek.Friday, [new(9, 0)], previewBefore);
            Screenshot("schedule-preview-weekly");
            Control<TextBox>("Zone").Text = "CI-invalid-time-zone";
            if (Control<TextBlock>("NextRun").Text.StartsWith("Ближайшие") || Control<TextBlock>("SchedulePreviewHint").IsVisible)
                throw new InvalidOperationException("Неизвестный часовой пояс показывает устаревшие даты.");
            Control<TextBox>("Zone").Text = "UTC";
            Control<TextBox>("DailyAt").Text = "09:00, 09:00";
            if (Control<TextBlock>("NextRun").Text.StartsWith("Ближайшие") || Control<TextBlock>("SchedulePreviewHint").IsVisible)
                throw new InvalidOperationException("Повторное время показывает устаревшие даты.");
            Control<CheckBox>("Enabled").IsChecked = false;
            if (!Control<TextBlock>("NextRun").Text.Contains("отключены"))
                throw new InvalidOperationException("Выключенное расписание показывает будущие запуски.");
            Control<ComboBox>("Schedule").SelectedValue = ScheduleKind.Manual;
            if (Control<TextBlock>("NextRun").Text != "Запуск только вручную." || Control<StackPanel>("TimePanel").IsVisible
                || Control<StackPanel>("ZonePanel").IsVisible || Control<TextBlock>("SchedulePreviewHint").IsVisible)
                throw new InvalidOperationException("Ручной режим неверно объясняет запуск.");
            Control<ComboBox>("Schedule").SelectedValue = ScheduleKind.Daily;
            Control<CheckBox>("Mon").IsChecked = true;
            Control<CheckBox>("Fri").IsChecked = false;
            Control<TextBox>("DailyAt").Text = "23:00, 09:00, 13:00";
            Control<TextBox>("Zone").Text = originalZone;
            Control<CheckBox>("Enabled").IsChecked = true;
            Control<TextBox>("KeepLast").Text = "0";
            Click("NextStepButton");
            if (!Control<TextBlock>("WizardError").IsVisible || !Control<StackPanel>("ScheduleStep").IsVisible)
                throw new InvalidOperationException("Неверное число копий не остановило мастер.");
            Control<TextBox>("KeepLast").Text = "7";
            Screenshot("wizard-schedule");
            window.Width = 900; window.Height = 580;
            AssertVisibleFooter("NextStepButton");
            Screenshot("wizard-compact");
            Control<TextBlock>("NextRun").BringIntoView();
            window.UpdateLayout();
            var preview = Control<TextBlock>("NextRun");
            var scroll = Control<ScrollViewer>("EditorScroll");
            var previewPosition = preview.TranslatePoint(new Point(), scroll);
            if (previewPosition.Y < 0 || previewPosition.Y + preview.ActualHeight > scroll.ActualHeight + 1)
                throw new InvalidOperationException("Ближайшие запуски недоступны в компактном мастере.");
            AssertVisibleFooter("NextStepButton");
            Screenshot("schedule-preview-compact");
            Click("NextStepButton");
            AssertVisibleFooter("SaveButton");
            if (!Control<TextBox>("WizardSummary").Text.Contains("ZIP с паролем") || !Control<TextBox>("WizardSummary").Text.Contains("7 успешных"))
                throw new InvalidOperationException("Итог мастера не показывает защиту и хранение.");
            if (!Control<TextBox>("WizardSummary").Text.Contains(Control<TextBlock>("NextRun").Text))
                throw new InvalidOperationException("Ближайшие запуски не перенесены в итог мастера.");
            Screenshot("wizard-review-compact");
            window.Width = 1100; window.Height = 740;
            Click("PreviousStepButton"); Click("NextStepButton");
            Click("SaveButton");
            await WaitUntil(() => Control<DataGrid>("Jobs").Items.Count == 1 && Control<Button>("RunButton").IsEnabled, "Расписание не сохранено.");
            var configured = (BackupJob)Control<DataGrid>("Jobs").Items[0];
            if (configured.KeepLast != 7 || configured.AdditionalDestinations![0].KeepLast != 2
                || !Control<TextBox>("WizardSummary").Text.Contains("копий: 2"))
                throw new InvalidOperationException("Разные количества копий не сохранены или не показаны в итоге.");
            if (configured.TemporaryDirectory != temporaryDirectory || !Control<TextBox>("WizardSummary").Text.Contains(temporaryDirectory))
                throw new InvalidOperationException("Временная папка не показана в итоге или не сохранена.");
            if (configured.ArchivePasswordKey is null)
                throw new InvalidOperationException("Ссылка на пароль архива не сохранена.");
            if (!configured.ScheduledTimes.SequenceEqual(new TimeOnly[] { new(9, 0), new(13, 0), new(23, 0) }))
                throw new InvalidOperationException("Несколько времён запуска не сохранены через интерфейс.");
            if (Control<Border>("EditorCard").IsVisible || !Control<Border>("JobListCard").IsVisible)
                throw new InvalidOperationException("Сохранение не вернуло список заданий.");
            Click("JobScheduleButton");
            await WaitUntil(() => window.SmokeReady && Control<DataGrid>("Jobs").SelectedItem is BackupJob { Enabled: false },
                "Расписание не выключилось из списка.");
            var paused = (await new JobStore(Path.Combine(DesktopPaths.DataDirectory, "jobs.json")).LoadAsync()).Single();
            if (JsonSerializer.Serialize(paused with { Enabled = configured.Enabled }) != JsonSerializer.Serialize(configured)
                || Control<DataGrid>("Copies").Items.Count != 0 || Control<DataGrid>("RunHistory").Items.Count != 0)
                throw new InvalidOperationException("Переключение расписания изменило настройки или создало копию.");
            Screenshot("job-schedule-disabled");
            Click("JobScheduleButton");
            await WaitUntil(() => window.SmokeReady && Control<DataGrid>("Jobs").SelectedItem is BackupJob { Enabled: true },
                "Расписание не включилось из списка.");
            Click("EditJobButton");
            Click("NextStepButton"); Click("NextStepButton");
            Control<ComboBox>("Schedule").SelectedValue = ScheduleKind.Manual;
            Click("NextStepButton"); Click("SaveButton");
            await WaitUntil(() => window.SmokeReady && Control<DataGrid>("Jobs").SelectedItem is BackupJob { ScheduleKind: ScheduleKind.Manual }
                && Control<Button>("RunButton").IsEnabled, "Задание не сохранено.");
            if (Control<Button>("JobScheduleButton").IsEnabled
                || !Control<Button>("JobScheduleButton").ToolTip.ToString()!.Contains("ручное"))
                throw new InvalidOperationException("Ручное задание допускает переключение отсутствующего расписания.");
            using (var lockedSource = new FileStream(Path.Combine(source, "1Cv8.1CD"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Click("CheckStorageButton");
                await WaitUntil(() => window.SmokeReady && Control<Expander>("StorageCheckResults").Header.ToString()!.StartsWith("Все хранилища доступны"),
                    "Проверка хранилищ не завершилась через интерфейс.");
            }
            if (Control<DataGrid>("Copies").Items.Count != 0 || Control<DataGrid>("RunHistory").Items.Count != 0
                || File.ReadAllText(temporaryMarker) != "keep unrelated files")
                throw new InvalidOperationException("Проверка доступа создала копию, запуск или изменила посторонний файл.");
            Control<Expander>("StorageCheckResults").IsExpanded = true;
            Screenshot("storage-readiness-success");
            Control<Expander>("StorageCheckResults").IsExpanded = false;
            Click("RunButton");
            await WaitUntil(() => Control<DataGrid>("RunHistory").Items.OfType<BackupRun>().Any(run => run.Status == BackupRunStatus.Succeeded)
                && window.SmokeReady, "Копия не создана через интерфейс.");
            if (!Control<TextBlock>("SelectedJobSummary").Text.Contains("во все 2 хранилища")
                || Control<ProgressBar>("JobProgress").Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("После копирования не показан понятный итог или остался индикатор работы.");
            var successfulCopies = Control<DataGrid>("Copies").Items.OfType<BackupCopyRow>().ToArray();
            var successfulRun = Control<DataGrid>("RunHistory").Items.OfType<BackupRun>().Single(run => run.Status == BackupRunStatus.Succeeded);
            if (Path.GetDirectoryName(successfulRun.WorkDirectory) != temporaryDirectory || Directory.Exists(successfulRun.WorkDirectory)
                || Directory.Exists(Path.Combine(DesktopPaths.DataDirectory, "work")) || File.ReadAllText(temporaryMarker) != "keep unrelated files")
                throw new InvalidOperationException("Копия не использовала выбранную временную папку или очистка затронула чужие файлы.");
            if (successfulCopies.Length != 2 || successfulCopies[0].Sha256 != successfulCopies[1].Sha256
                || !Directory.GetFiles(secondaryDirectory).Any())
                throw new InvalidOperationException("Один и тот же архив не доставлен в оба хранилища.");
            Control<TextBox>("CopySearch").Text = "no-such-copy-before-navigation";
            Control<ComboBox>("CopyStorageFilter").SelectedIndex = 3;
            if (Control<DataGrid>("Copies").Items.Count != 0 || !Control<Button>("ShowLatestCopyButton").IsEnabled)
                throw new InvalidOperationException("Фильтр каталога заблокировал переход к копии из задания.");
            Click("ShowLatestCopyButton");
            await WaitUntil(() => window.SmokeReady && Control<DataGrid>("Copies").SelectedItem is BackupCopyRow,
                "Переход к последней копии не выбрал файл.");
            var selectedCopy = (BackupCopyRow)Control<DataGrid>("Copies").SelectedItem;
            var savedJob = (BackupJob)Control<DataGrid>("Jobs").SelectedItem;
            if (selectedCopy.Record.JobId != savedJob.Id || Control<Border>("CopiesView").Visibility != Visibility.Visible)
                throw new InvalidOperationException("Переход к копии выбрал другое задание.");
            if (Control<TextBox>("CopySearch").Text.Length != 0 || Control<ComboBox>("CopyStorageFilter").SelectedIndex != 0
                || Control<ComboBox>("CopyJobFilter").SelectedValue is not Guid scope || scope != savedJob.Id)
                throw new InvalidOperationException("Переход к последней копии сохранил скрывающие её фильтры.");
            Control<Expander>("CopyDetailsPanel").IsExpanded = true;
            Screenshot("first-copy-result");
            Control<Expander>("CopyDetailsPanel").IsExpanded = false;
            Click("JobsNavigation");
            // A locked source must produce a visible failure without creating another copy.
            using (var lockedSource = new FileStream(Path.Combine(source, "1Cv8.1CD"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Click("RunButton");
                await WaitUntil(() => Control<DataGrid>("RunHistory").Items.OfType<BackupRun>().Any(run => run.Status == BackupRunStatus.Failed)
                    && window.SmokeReady, "Ошибка занятого файла не появилась в истории.");
            }
            if (!Control<TextBlock>("SelectedJobSummary").Text.Contains("завершилось с ошибкой"))
                throw new InvalidOperationException("После ошибки выбранное задание показывает успешное копирование.");
            Control<ComboBox>("HistoryStatusFilter").SelectedIndex = 2;
            Control<TextBox>("HistorySearch").Text = "no-such-job-before-result-navigation";
            Click("ShowLatestRunButton");
            await WaitUntil(() => window.SmokeReady && Control<DataGrid>("RunHistory").SelectedItem is BackupRun { Status: BackupRunStatus.Failed },
                "Переход к результату не открыл последнюю ошибку после фильтра успешных запусков.");
            if (Control<ComboBox>("HistoryStatusFilter").SelectedIndex != 0 || Control<TextBox>("HistorySearch").Text.Length != 0
                || !Control<TextBlock>("HistorySummary").Text.Contains("на этом компьютере"))
                throw new InvalidOperationException("Переход к результату сохранил скрывающий запись фильтр или неверную подпись времени.");
            Screenshot("failed-copy-result");
            Control<ComboBox>("HistoryStatusFilter").SelectedIndex = 1;
            var filtered = Control<DataGrid>("RunHistory");
            if (filtered.Items.Count != 1 || filtered.Items[0] is not BackupRun { Status: BackupRunStatus.Failed })
                throw new InvalidOperationException("Фильтр ошибок показывает неверные записи.");
            filtered.SelectedIndex = 0;
            if (!Control<TextBox>("RunDetails").Text.Contains("Ошибка:"))
                throw new InvalidOperationException("Подробности ошибки недоступны.");
            var exportedPath = Path.Combine(output, "history-export.json");
            await window.ExportHistoryAsync(exportedPath);
            using (var exported = JsonDocument.Parse(File.ReadAllText(exportedPath)))
            {
                var runs = exported.RootElement.GetProperty("Runs");
                if (runs.GetArrayLength() != 1 || runs[0].GetProperty("Error").GetString() is not { Length: > 0 })
                    throw new InvalidOperationException("Экспорт не соответствует фильтру истории.");
            }
            try
            {
                await window.ExportHistoryAsync(Path.Combine(DesktopPaths.DataDirectory, "runs.json"));
                throw new InvalidOperationException("Экспорт разрешил перезаписать рабочий журнал.");
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("Выберите папку вне")) { }
            Control<TextBox>("HistorySearch").Text = "no-such-job-or-error-203a8b";
            if (filtered.Items.Count != 0 || Control<Button>("ExportHistoryButton").IsEnabled)
                throw new InvalidOperationException("Поиск в истории не скрывает неподходящие записи.");
            Control<TextBox>("HistorySearch").Text = "";
            Control<ComboBox>("HistoryStatusFilter").SelectedIndex = 0;
            foreach (var (navigation, name) in new[] { ("JobsNavigation", "jobs"), ("CopiesNavigation", "copies"),
                ("HistoryNavigation", "history"), ("AgentNavigation", "agent") })
            {
                Click(navigation);
                await Task.Delay(200);
                Screenshot(name);
            }
            Click("CopiesNavigation");
            // Select while the asynchronous refresh is pending; the refresh must keep the user's latest selection.
            Control<DataGrid>("Copies").SelectedIndex = 0;
            var selectedCopyId = (Control<DataGrid>("Copies").SelectedItem as BackupCopyRow)?.Record.Id;
            await WaitUntil(() => Control<DataGrid>("Copies").Items.Count == 2 && window.SmokeReady, "Каталог копий не обновился.");
            if (selectedCopyId is null || (Control<DataGrid>("Copies").SelectedItem as BackupCopyRow)?.Record.Id != selectedCopyId)
                throw new InvalidOperationException("Обновление каталога сбросило выбранную пользователем копию.");
            Click("VerifyCopyButton");
            await WaitUntil(() => Control<TextBlock>("CopyVerificationStatus").Text.StartsWith("Размер и SHA256 совпадают"), "Проверка копии не завершилась.");
            var extractParent = Path.Combine(DesktopPaths.DataDirectory, "extraction");
            Directory.CreateDirectory(extractParent);
            if (!Control<Button>("ExtractOneCButton").IsEnabled)
                throw new InvalidOperationException("Извлечение недоступно: " + Control<TextBlock>("CopyVerificationStatus").Text);
            await window.ExtractSelectedCopyAsync(extractParent);
            var extracted = Directory.GetFiles(extractParent, "1Cv8.1CD", SearchOption.AllDirectories);
            if (extracted.Length != 1 || File.ReadAllText(extracted[0]) != File.ReadAllText(Path.Combine(source, "1Cv8.1CD")))
                throw new InvalidOperationException("База не извлечена из проверенного архива через интерфейс: "
                    + Control<TextBlock>("CopyVerificationStatus").Text);
            var encryptedRecord = ((BackupCopyRow)Control<DataGrid>("Copies").SelectedItem).Record;
            if (encryptedRecord.Backup.ArchivePasswordKey is null)
                throw new InvalidOperationException("Каталог не сохранил ссылку на пароль.");
            File.Copy(encryptedRecord.Backup.Location, Path.Combine(output, "encrypted-fixture.zip"));
            if (File.ReadAllText(exportedPath).Contains("CI-only-архив"))
                throw new InvalidOperationException("Пароль попал в экспорт истории.");
            // Cancelling edits must retain the saved model used by backup execution.
            Click("JobsNavigation"); Click("EditJobButton");
            Control<TextBox>("JobName").Text = "unsaved draft must be discarded";
            Click("NextStepButton");
            Control<Expander>("AdditionalStorageOptions").IsExpanded = true;
            Control<TextBox>("TemporaryFolder").Text = Path.Combine(DesktopPaths.DataDirectory, "unsaved temporary");
            Control<ListBox>("AdditionalStorageList").SelectedIndex = 0;
            Click("RemoveStorageButton");
            Click("CancelEditorButton");
            if (Control<TextBox>("TemporaryFolder").Text != temporaryDirectory
                || ((BackupJob)Control<DataGrid>("Jobs").SelectedItem).TemporaryDirectory != temporaryDirectory)
                throw new InvalidOperationException("Отмена мастера изменила временную папку.");
            if (Control<DataGrid>("Jobs").SelectedItem is not BackupJob { Name: "Тестовая база 1С" }
                || Control<TextBox>("JobName").Text != "Тестовая база 1С" || Control<Border>("EditorCard").IsVisible)
                throw new InvalidOperationException("Отмена мастера изменила сохранённое задание.");
            if (Control<ListBox>("AdditionalStorageList").Items.Count != 1
                || ((BackupJob)Control<DataGrid>("Jobs").SelectedItem).AdditionalDestinations?.Count != 1)
                throw new InvalidOperationException("Отмена удаления хранилища изменила сохранённые назначения.");
            // One destination is a fixture file instead of a directory; the other two must still receive copies.
            var blockedDirectory = Path.Combine(DesktopPaths.DataDirectory, "blocked-storage");
            File.WriteAllText(blockedDirectory, "fixture file, not a directory");
            Click("EditJobButton"); Click("NextStepButton");
            SetAdditionalStorage(blockedDirectory);
            Click("NextStepButton"); Click("NextStepButton"); Click("SaveButton");
            await WaitUntil(() => window.SmokeReady && Control<Button>("RunButton").IsEnabled, "Третье хранилище не сохранено.");
            var copiesBeforeCheck = Control<DataGrid>("Copies").Items.Count;
            var runsBeforeCheck = Control<DataGrid>("RunHistory").Items.Count;
            Click("CheckStorageButton");
            await WaitUntil(() => window.SmokeReady && Control<Expander>("StorageCheckResults").Header.ToString()!.Contains("2 из 3"),
                "Недоступное хранилище не показано проверкой доступа.");
            if (!Control<TextBox>("StorageCheckDetails").Text.Contains(blockedDirectory)
                || Control<DataGrid>("Copies").Items.Count != copiesBeforeCheck || Control<DataGrid>("RunHistory").Items.Count != runsBeforeCheck)
                throw new InvalidOperationException("Проверка доступа потеряла ошибку или изменила журнал копий / запусков.");
            if (Control<TextBox>("StorageCheckDetails").Text.IndexOf(blockedDirectory, StringComparison.Ordinal)
                > Control<TextBox>("StorageCheckDetails").Text.IndexOf(Path.Combine(DesktopPaths.DataDirectory, "copies"), StringComparison.Ordinal))
                throw new InvalidOperationException("Ошибка хранилища скрыта ниже успешных результатов.");
            Screenshot("storage-readiness-failure");
            Control<Expander>("StorageCheckResults").IsExpanded = false;
            Click("RunButton");
            await WaitUntil(() => window.SmokeReady && Control<TextBlock>("SelectedJobSummary").Text.Contains("в 2 из 3 хранилищ"), "Частичная доставка не показана.");
            if (Control<DataGrid>("Copies").Items.Count != 4)
                throw new InvalidOperationException("Ошибка третьего хранилища помешала другим копиям.");
            Click("ShowLatestRunButton");
            await WaitUntil(() => Control<DataGrid>("RunHistory").SelectedItem is BackupRun { Status: BackupRunStatus.PartiallySucceeded }, "Не выбран частичный результат.");
            Control<ComboBox>("HistoryStatusFilter").SelectedIndex = 1;
            if (Control<DataGrid>("RunHistory").Items.Count != 2 || !Control<TextBox>("RunDetails").Text.Contains("Доставка по хранилищам:"))
                throw new InvalidOperationException("Частичная ошибка не попала в фильтр проблем или потеряла результаты доставки.");
            Screenshot("multiple-storage-partial-result");
            Click("JobsNavigation"); Click("RunButton");
            await WaitUntil(() => window.SmokeReady && Control<DataGrid>("Copies").Items.Count == 5,
                "Отдельное количество копий не применилось после третьей доставки.");
            if (Directory.GetFiles(Path.Combine(DesktopPaths.DataDirectory, "copies")).Length != 3
                || Directory.GetFiles(secondaryDirectory).Length != 2)
                throw new InvalidOperationException("Хранилища не сохраняют своё количество файлов.");
            Screenshot("per-storage-retention-result");
            // Start the two-job scenario with all records visible.
            Click("CopiesNavigation");
            await WaitUntil(() => window.SmokeReady, "Каталог не обновился перед копированием задания.");
            Click("ResetCopyFiltersButton"); Click("JobsNavigation");
            // A copied draft must be independent, while sharing the existing immutable password binding.
            var original = (BackupJob)Control<DataGrid>("Jobs").SelectedItem;
            var originalJson = JsonSerializer.Serialize(original);
            Click("DuplicateJobButton");
            if (Control<TextBox>("JobName").Text != original.Name + " — копия"
                || Control<CheckBox>("Enabled").IsChecked != false
                || Control<TextBox>("TemporaryFolder").Text != original.TemporaryDirectory
                || Control<ListBox>("AdditionalStorageList").Items.Count != 2
                || Control<Button>("RunButton").IsEnabled)
                throw new InvalidOperationException("Новое задание потеряло настройки или допускает запуск несохранённого черновика.");
            Screenshot("duplicate-job-start");
            Control<TextBox>("TemporaryFolder").Text = "unsaved duplicate folder";
            Click("CancelEditorButton");
            if (Control<DataGrid>("Jobs").Items.Count != 1
                || JsonSerializer.Serialize(Control<DataGrid>("Jobs").SelectedItem as BackupJob) != originalJson
                || Control<TextBox>("TemporaryFolder").Text != original.TemporaryDirectory)
                throw new InvalidOperationException("Отмена копирования изменила исходное задание.");
            Click("DuplicateJobButton");
            var secondSource = Path.Combine(DesktopPaths.DataDirectory, "second-base");
            Directory.CreateDirectory(secondSource);
            File.WriteAllText(Path.Combine(secondSource, "1Cv8.1CD"), "second isolated database fixture");
            Control<TextBox>("Source").Text = secondSource;
            Click("NextStepButton"); Click("NextStepButton");
            Control<TextBox>("KeepLast").Text = "1";
            Click("NextStepButton");
            Screenshot("duplicate-job-review");
            Click("SaveButton");
            await WaitUntil(() => window.SmokeReady && Control<DataGrid>("Jobs").Items.Count == 2 && Control<Button>("RunButton").IsEnabled,
                "Копия задания не сохранена отдельно.");
            var duplicate = (BackupJob)Control<DataGrid>("Jobs").SelectedItem;
            if (duplicate.Id == original.Id || duplicate.Enabled || duplicate.Source != secondSource
                || JsonSerializer.Serialize(duplicate with { Id = original.Id, Name = original.Name, Source = original.Source,
                    Enabled = original.Enabled, KeepLast = original.KeepLast }) != originalJson
                || JsonSerializer.Serialize(Control<DataGrid>("Jobs").Items.OfType<BackupJob>().Single(job => job.Id == original.Id)) != originalJson)
                throw new InvalidOperationException("Копия задания потеряла настройки, пароль или изменила оригинал.");
            // Both jobs use the same folders. Retention of the duplicate must preserve the original's copies.
            Click("RunButton");
            await WaitUntil(() => window.SmokeReady && Control<DataGrid>("Copies").Items.Count == 7, "Копия задания не доставила защищённый архив.");
            Click("RunButton");
            await WaitUntil(() => window.SmokeReady && Control<DataGrid>("Copies").Items.Count == 8, "Хранение копии задания не применилось независимо.");
            if (Directory.GetFiles(Path.Combine(DesktopPaths.DataDirectory, "copies")).Length != 4
                || Directory.GetFiles(secondaryDirectory).Length != 4)
                throw new InvalidOperationException("Копия задания удалила резервные копии исходного задания.");
            Control<DataGrid>("Jobs").SelectedItem = Control<DataGrid>("Jobs").Items.OfType<BackupJob>().Single(job => job.Id == original.Id);
            Click("DuplicateJobButton");
            if (Control<TextBox>("JobName").Text != original.Name + " — копия 2")
                throw new InvalidOperationException("Повторное копирование не предложило уникальное название.");
            Click("CancelEditorButton");
            Screenshot("duplicate-job-saved");
            // Identical user-facing names must still select one database by its stable identifier.
            Control<DataGrid>("Jobs").SelectedItem = Control<DataGrid>("Jobs").Items.OfType<BackupJob>().Single(job => job.Id == duplicate.Id);
            Click("EditJobButton");
            Control<TextBox>("JobName").Text = original.Name;
            Click("NextStepButton"); Click("NextStepButton"); Click("NextStepButton"); Click("SaveButton");
            await WaitUntil(() => window.SmokeReady && Control<Button>("RunButton").IsEnabled, "Переименование второй базы не сохранилось.");
            Control<DataGrid>("Jobs").SelectedItem = Control<DataGrid>("Jobs").Items.OfType<BackupJob>().Single(job => job.Id == original.Id);
            Click("CopiesNavigation");
            await WaitUntil(() => window.SmokeReady, "Каталог двух заданий не загрузился.");
            Control<ComboBox>("CopyJobFilter").SelectedValue = duplicate.Id;
            if (Control<DataGrid>("Copies").Items.Count != 3
                || Control<DataGrid>("Copies").Items.OfType<BackupCopyRow>().Any(row => row.Record.JobId != duplicate.Id)
                || !Control<ComboBox>("CopyJobFilter").Text.Contains(duplicate.Id.ToString("N")[..8]))
                throw new InvalidOperationException("Фильтр задания смешал копии двух баз.");
            Control<TextBox>("CopySearch").Text = "SECOND-COPIES";
            Control<ComboBox>("CopyStorageFilter").SelectedIndex = 1;
            if (Control<DataGrid>("Copies").Items.Count != 2)
                throw new InvalidOperationException("Поиск по пути не выбрал второе хранилище без учёта регистра.");
            Control<DataGrid>("Copies").SelectedIndex = 0;
            var filteredCopyId = ((BackupCopyRow)Control<DataGrid>("Copies").SelectedItem).Record.Id;
            Click("RefreshCopiesButton");
            await WaitUntil(() => window.SmokeReady, "Отфильтрованный каталог не обновился.");
            if (Control<DataGrid>("Copies").Items.Count != 2
                || Control<ComboBox>("CopyJobFilter").SelectedValue is not Guid filterId || filterId != duplicate.Id
                || (Control<DataGrid>("Copies").SelectedItem as BackupCopyRow)?.Record.Id != filteredCopyId)
                throw new InvalidOperationException("Обновление потеряло фильтры или выбранную копию.");
            Screenshot("copies-filtered");
            Control<ComboBox>("CopyStorageFilter").SelectedIndex = 2;
            if (Control<DataGrid>("Copies").Items.Count != 0 || Control<DataGrid>("Copies").SelectedItem is not null
                || Control<Button>("VerifyCopyButton").IsEnabled || !Control<TextBlock>("CopiesSummary").Text.Contains("по выбранным условиям нет"))
                throw new InvalidOperationException("Пустой фильтр оставил действия над скрытой копией.");
            Click("ResetCopyFiltersButton");
            if (Control<DataGrid>("Copies").Items.Count != 8)
                throw new InvalidOperationException("Сброс фильтров потерял копии.");
            Control<DataGrid>("Copies").SelectedIndex = 0;
            // Same names must not allow the schedule toggle to update the wrong job.
            Click("JobsNavigation");
            Control<DataGrid>("Jobs").SelectedItem = Control<DataGrid>("Jobs").Items.OfType<BackupJob>().Single(job => job.Id == duplicate.Id);
            Click("EditJobButton"); Click("NextStepButton"); Click("NextStepButton");
            Control<ComboBox>("Schedule").SelectedValue = ScheduleKind.Daily;
            Control<TextBox>("DailyAt").Text = "09:00, 13:00, 23:00";
            Control<CheckBox>("Enabled").IsChecked = false;
            Click("NextStepButton"); Click("SaveButton");
            await WaitUntil(() => window.SmokeReady && Control<Button>("JobScheduleButton").IsEnabled, "Расписание второй базы не сохранено.");
            var beforeToggle = (BackupJob)Control<DataGrid>("Jobs").SelectedItem;
            var runCountBeforeToggle = Control<DataGrid>("RunHistory").Items.Count;
            Click("JobScheduleButton");
            await WaitUntil(() => window.SmokeReady && Control<DataGrid>("Jobs").SelectedItem is BackupJob { Enabled: true },
                "Расписание второй базы не включилось.");
            var savedJobs = await new JobStore(Path.Combine(DesktopPaths.DataDirectory, "jobs.json")).LoadAsync();
            if (JsonSerializer.Serialize(savedJobs.Single(job => job.Id == original.Id)) != originalJson
                || JsonSerializer.Serialize(savedJobs.Single(job => job.Id == duplicate.Id) with { Enabled = false }) != JsonSerializer.Serialize(beforeToggle)
                || !Control<TextBlock>("JobsSummary").Text.Contains("По расписанию: 1")
                || !Control<TextBlock>("SelectedJobSummary").Text.Contains("включите автоматическое копирование"))
                throw new InvalidOperationException("Переключение расписания смешало задания или потеряло настройки.");
            Screenshot("job-schedule-enabled");
            Click("JobScheduleButton");
            await WaitUntil(() => window.SmokeReady && Control<DataGrid>("Jobs").SelectedItem is BackupJob { Enabled: false },
                "Расписание второй базы не выключилось.");
            if (Control<DataGrid>("Copies").Items.Count != 8 || Control<DataGrid>("RunHistory").Items.Count != runCountBeforeToggle
                || !Control<Button>("RunButton").IsEnabled)
                throw new InvalidOperationException("Отключение расписания изменило копии, историю или запретило ручной запуск.");
            Control<DataGrid>("Jobs").SelectedItem = Control<DataGrid>("Jobs").Items.OfType<BackupJob>().Single(job => job.Id == original.Id);
            window.Width = 900; window.Height = 580;
            foreach (var navigation in new[] { "JobsNavigation", "CopiesNavigation", "HistoryNavigation", "AgentNavigation" })
            {
                if (navigation == "CopiesNavigation")
                {
                    Click("ShowLatestCopyButton");
                    await WaitUntil(() => window.SmokeReady, "Переход к копии не завершился в компактном окне.");
                }
                else Click(navigation);
                await Task.Delay(100);
                Screenshot("compact-" + navigation);
                if (navigation == "AgentNavigation")
                {
                    AssertVisibleFooter("CopyBuildInfoButton");
                    Click("CopyBuildInfoButton");
                    if (Clipboard.GetText() != ApplicationBuildInfo.SupportText)
                        throw new InvalidOperationException("Сведения недоступны в компактном окне.");
                }
                if (navigation == "JobsNavigation")
                {
                    var grid = Control<DataGrid>("Jobs");
                    window.UpdateLayout();
                    foreach (var job in grid.Items.OfType<BackupJob>())
                    {
                        if (grid.ItemContainerGenerator.ContainerFromItem(job) is not DataGridRow row
                            || row.ActualHeight <= 0 || row.TranslatePoint(new Point(), grid).Y < 0
                            || row.TranslatePoint(new Point(), grid).Y + row.ActualHeight > grid.ActualHeight + 1)
                            throw new InvalidOperationException("Панели скрыли строки заданий в компактном окне.");
                    }
                }
                if (navigation == "CopiesNavigation")
                {
                    Control<Expander>("CopyDetailsPanel").IsExpanded = true;
                    var grid = Control<DataGrid>("Copies");
                    window.UpdateLayout();
                    Screenshot("compact-copies-expanded");
                    if (grid.ItemContainerGenerator.ContainerFromIndex(0) is not DataGridRow row
                        || row.ActualHeight <= 0 || row.TranslatePoint(new Point(), grid).Y < 0
                        || row.TranslatePoint(new Point(), grid).Y + row.ActualHeight > grid.ActualHeight + 1)
                        throw new InvalidOperationException("Подробности скрыли таблицу копий в компактном окне.");
                    Control<Expander>("CopyDetailsPanel").IsExpanded = false;
                }
            }
            await VerifyPostgreSqlMonitoringAsync(window);
            Finish(output, true, "Три ближайших запуска, смена дней / часового пояса / режима и итог, компактное окно, мастер и создание задания на основе существующего, отмена, независимое хранение копий двух баз, несколько времён, ZIP AES-256, сохранение пароля Windows, извлечение, ошибка занятой базы, история, каталог, SHA256, проверка хранилищ и импорт SQL-отчёта проверены на Windows.");
        }
        catch (Exception ex) { Finish(output, false, ex.ToString()); }
        finally
        {
            // Remove only isolated CI password bindings, never the user's credentials.
            try
            {
                if (archiveCredentialKey is not null) new WindowsArchivePasswordStore().Delete(archiveCredentialKey);
            }
            catch (Exception) { }
        }
    }

    private static async Task VerifyPostgreSqlMonitoringAsync(MainWindow owner)
    {
        var directory = Path.Combine(DesktopPaths.DataDirectory, "sql-reports");
        Directory.CreateDirectory(directory);
        var observation = new ExternalBackupObservation("ci-pg-backup", "ci-cluster", "ci-database", "LogicalFull",
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow, "UTC", ExternalBackupOutcome.Completed,
            128, ["D:\\Backups\\fixture.dump"], "PgRestoreCatalogChecked");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        await File.WriteAllTextAsync(Path.Combine(directory, "fixture.json"), JsonSerializer.Serialize(observation, options));
        var settings = await new AgentSettingsStore(Path.Combine(DesktopPaths.DataDirectory, "agent.json")).LoadAsync();
        var monitorWindow = new ExternalMonitoringWindow(settings) { Owner = owner };
        monitorWindow.Show();
        try
        {
            await WaitUntil(() => monitorWindow.SmokeReady, "Мониторинг не загрузился.");
            T Control<T>(string name) where T : FrameworkElement => (T)monitorWindow.FindName(name);
            void Click(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Control<ComboBox>("SourceKind").SelectedValue = ExternalBackupSourceKind.PostgreSqlReports;
            Control<TextBox>("MonitorName").Text = "Тестовый монитор PostgreSQL";
            Control<TextBox>("ReportDirectory").Text = directory;
            Click("SaveMonitorButton");
            await WaitUntil(() => monitorWindow.SmokeReady && Control<ComboBox>("Sources").Items.Count == 1,
                "Источник мониторинга не сохранён.");
            Click("CollectMonitorButton");
            await WaitUntil(() => monitorWindow.SmokeReady && Control<DataGrid>("Reports").Items.Count == 1,
                "Отчёт PostgreSQL не импортирован.");
            Control<DataGrid>("Reports").SelectedIndex = 0;
            if (!Control<TextBox>("ReportDetails").Text.Contains("ci-cluster"))
                throw new InvalidOperationException("Подробности наблюдения SQL не отображаются.");
            Click("CollectMonitorButton");
            await WaitUntil(() => monitorWindow.SmokeReady, "Повторная проверка SQL не завершилась.");
            if (Control<DataGrid>("Reports").Items.Count != 1)
                throw new InvalidOperationException("Мониторинг дублирует внешний отчёт.");
        }
        finally { monitorWindow.Close(); }
    }
}
