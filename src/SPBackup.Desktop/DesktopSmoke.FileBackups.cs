using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SPBackup.Core;

namespace SPBackup.Desktop;

internal static partial class DesktopSmoke
{
    private static async Task VerifyFileBackupsAsync(MainWindow window, Action<string> screenshot)
    {
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Click(string name)
        {
            window.UpdateLayout();
            var button = Control<Button>(name);
            if (!button.IsEnabled || !button.IsVisible) throw new InvalidOperationException("Кнопка недоступна: " + name);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
        }
        var fixture = Path.GetDirectoryName(DesktopPaths.DataDirectory)!;
        var source = Path.Combine(fixture, "ordinary-files"); Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        await File.WriteAllTextAsync(Path.Combine(source, "stable.txt"), "unchanged");
        await File.WriteAllTextAsync(Path.Combine(source, "change.txt"), "before");
        await File.WriteAllTextAsync(Path.Combine(source, "remove.txt"), "removed");
        Click("JobsNavigation"); Click("NewJobButton");
        Control<TextBox>("JobName").Text = "Документы — инкрементная копия";
        Control<ComboBox>("Kind").SelectedItem = DatabaseKind.FilesAndFolders;
        Control<TextBox>("Source").Text = source;
        Control<ComboBox>("FileBackupModePicker").SelectedValue = FileBackupMode.Incremental;
        Control<TextBox>("FullBackupEvery").Text = "0";
        Click("NextStepButton");
        if (!Control<StackPanel>("BaseStep").IsVisible || !Control<TextBlock>("WizardError").IsVisible)
            throw new InvalidOperationException("Мастер принял нулевую длину инкрементной цепочки.");
        Control<TextBox>("FullBackupEvery").Text = "3";
        window.Width = 900; window.Height = 580; window.UpdateLayout();
        VerifyGuideLink(window, "WizardHelpButton", 2, "Сделать первую копию на безопасном примере");
        foreach (var name in new[] { "FileBackupModePicker", "FullBackupEvery", "FullBackupDayPicker" })
        {
            var element = Control<FrameworkElement>(name); var scroll = Control<ScrollViewer>("EditorScroll");
            var point = element.TranslatePoint(new Point(), scroll);
            if (!element.IsVisible || point.Y < 0 || point.Y + element.ActualHeight > scroll.ActualHeight + 1)
                throw new InvalidOperationException("Файловый режим не виден без прокрутки в компактном мастере: " + name);
        }
        Control<ComboBox>("FullBackupDayPicker").SelectedValue = (int)DayOfWeek.Friday;
        if (Control<TextBox>("FullBackupEvery").IsEnabled)
            throw new InvalidOperationException("Выбор дня недели не отключил план по числу архивов.");
        screenshot("files-incremental-source-compact");
        Click("NextStepButton");
        Control<TextBox>("Destination").Text = Path.Combine(fixture, "ordinary-copies");
        VerifyGuideLink(window, "WizardHelpButton", 8, "Использовать внешний диск или сетевую папку");
        Control<TextBox>("TemporaryFolder").Text = Path.Combine(fixture, "ordinary-HDD-work");
        Click("NextStepButton");
        Control<TextBox>("KeepLast").Text = "1"; Control<CheckBox>("Enabled").IsChecked = false;
        VerifyGuideLink(window, "WizardHelpButton", 4, "Настроить ежедневное копирование");
        Click("NextStepButton");
        VerifyGuideLink(window, "WizardHelpButton", 3, "Убедиться, что копия действительно восстанавливается");
        if (!Control<TextBox>("WizardSummary").Text.Contains("Инкремент") || !Control<TextBox>("WizardSummary").Text.Contains("Пятница"))
            throw new InvalidOperationException("Итог мастера потерял файловый режим и длину цепочки.");
        screenshot("files-incremental-review-compact");
        Click("SaveButton");
        await WaitUntil(() => window.SmokeReady && Control<Border>("JobListCard").IsVisible, "Файловое задание не сохранено.");
        var store = new JobStore(Path.Combine(DesktopPaths.DataDirectory, "jobs.json"));
        var job = (await store.LoadAsync()).Single(job => job.Name == "Документы — инкрементная копия");
        if (job.Kind != DatabaseKind.FilesAndFolders || job.FileBackupMode != FileBackupMode.Incremental || job.FullBackupEvery != 3 || job.FullBackupDayOfWeek != DayOfWeek.Friday)
            throw new InvalidOperationException("Файловые настройки потеряны при сохранении.");
        Control<DataGrid>("Jobs").SelectedItem = Control<DataGrid>("Jobs").Items.OfType<BackupJob>().Single(item => item.Id == job.Id);
        if (!Control<TextBlock>("SelectedJobSummary").Text.Contains("Полная: пятница"))
            throw new InvalidOperationException("Карточка задания не показывает день полной копии.");
        Click("EditJobButton");
        if ((int?)Control<ComboBox>("FullBackupDayPicker").SelectedValue != (int)DayOfWeek.Friday
            || Control<TextBox>("FullBackupEvery").IsEnabled)
            throw new InvalidOperationException("Редактор потерял сохранённый день полной копии.");
        Control<ComboBox>("FullBackupDayPicker").SelectedValue = -1;
        if (!Control<TextBox>("FullBackupEvery").IsEnabled)
            throw new InvalidOperationException("Возврат к числу архивов не включил настройку цепочки.");
        Click("NextStepButton"); Click("NextStepButton"); Click("NextStepButton"); Click("SaveButton");
        await WaitUntil(() => window.SmokeReady && Control<Border>("JobListCard").IsVisible, "Изменение плана полной копии не сохранено.");
        job = (await store.LoadAsync()).Single(item => item.Id == job.Id);
        if (job.FullBackupDayOfWeek is not null) throw new InvalidOperationException("Отмена выбора дня не сохранена.");
        Control<DataGrid>("Jobs").SelectedItem = Control<DataGrid>("Jobs").Items.OfType<BackupJob>().Single(item => item.Id == job.Id);
        if (!Control<TextBlock>("SelectedJobSummary").Text.Contains("до 3 архивов в цепочке"))
            throw new InvalidOperationException("Карточка задания не обновила план полной копии.");
        var catalog = new BackupCatalog(Path.Combine(DesktopPaths.DataDirectory, "copies.json"));
        var history = new BackupRunHistory(Path.Combine(DesktopPaths.DataDirectory, "runs.json"));
        // A separate host owns the engine, as a background process or Windows service would.
        // This saved job has its schedule disabled and must still accept a manual command.
        using (var stopHost = new CancellationTokenSource())
        {
            var host = Task.Run(() => new LocalAgentHost(DesktopPaths.DataDirectory,
                new DatabaseBackupExecutor(new BackupProcessRunner()), new WindowsFtpCredentialStore())
                .RunAsync(cancellationToken: stopHost.Token));
            try
            {
                await WaitUntil(() => window.SmokeBackgroundManualReady
                    && Control<Button>("RunButton").IsEnabled, "Фоновый агент блокирует ручное задание без расписания.");
                await WaitUntil(() => window.SmokeBackgroundStorageReady && Control<Button>("CheckStorageButton").IsEnabled,
                    "Работающий агент не принимает проверку хранилищ.");
                var beforeProbeHistory = await history.ReadAsync(); var beforeProbeCatalog = await catalog.ReadAsync();
                Click("CheckStorageButton");
                await WaitUntil(() => window.SmokeReady && Control<Expander>("StorageCheckResults").Header.ToString()!.Contains("доступны (агент)"),
                    "Проверка хранилищ через агента не завершилась.");
                if (!Control<TextBox>("StorageCheckDetails").Text.Contains("Проверка через работающего агента.")
                    || !Control<TextBox>("StorageCheckDetails").Text.Contains("Пользователь:")
                    || beforeProbeHistory.Count != (await history.ReadAsync()).Count || beforeProbeCatalog.Count != (await catalog.ReadAsync()).Count
                    || host.IsCompleted)
                    throw new InvalidOperationException("Проверка не показывает своего исполнителя или создаёт копию/останавливает агент.");
                Control<Expander>("StorageCheckResults").IsExpanded = true;
                await Task.Delay(100); window.UpdateLayout(); Control<Expander>("StorageCheckResults").BringIntoView();
                window.UpdateLayout(); screenshot("storage-check-through-agent");
                Control<Expander>("StorageCheckResults").IsExpanded = false;
                Click("RunButton");
                if (Control<Button>("RunButton").IsEnabled) throw new InvalidOperationException("Повторный щелчок дублирует принятый ручной запуск.");
                await WaitUntil(() => window.SmokeReady && Control<Button>("RunButton").IsEnabled,
                    "Агент не подтвердил завершение ручного копирования.");
                if (host.IsCompleted) throw new InvalidOperationException("Ручной запуск остановил фоновый агент.");
                screenshot("manual-job-with-background-schedule");
            }
            finally { stopHost.Cancel(); await host.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
        await WaitUntil(() => Control<Button>("CheckStorageButton").IsEnabled, "Окно не освободило исполнитель после остановки агента.");
        var first = (await catalog.ReadAsync()).Single(record => record.JobId == job.Id);
        if (first.Backup.FileSnapshot?.IsFull != true) throw new InvalidOperationException("Первая копия не полная.");
        await File.WriteAllTextAsync(Path.Combine(source, "change.txt"), "after"); File.Delete(Path.Combine(source, "remove.txt"));
        await File.WriteAllTextAsync(Path.Combine(source, "added.txt"), "added");
        await WaitUntil(() => window.SmokeReady && Control<Button>("RunButton").IsEnabled, "Повторный ручной запуск файлов не готов.");
        Click("RunButton"); await WaitUntil(() => window.SmokeReady, "Инкрементная файловая копия не завершилась.");
        var copies = (await catalog.ReadAsync()).Where(record => record.JobId == job.Id).ToArray();
        var latest = copies.Single(record => record.Backup.FileSnapshot?.Depth == 1);
        if (copies.Length != 2 || latest.Backup.FileSnapshot!.ParentSnapshotId != first.Backup.FileSnapshot!.SnapshotId)
            throw new InvalidOperationException("Лимит хранения удалил основу файловой цепочки.");
        var beforeCatalog = JsonSerializer.Serialize(await catalog.ReadAsync()); var beforeHistory = JsonSerializer.Serialize(await history.ReadAsync());
        Click("CopiesNavigation"); await WaitUntil(() => window.SmokeReady, "Каталог файловых копий не загрузился.");
        Control<ComboBox>("CopyJobFilter").SelectedValue = job.Id;
        Control<DataGrid>("Copies").SelectedItem = Control<DataGrid>("Copies").Items.OfType<BackupCopyRow>().Single(row => row.Record.Id == latest.Id);
        await WaitUntil(() => window.SmokeReady && Control<Button>("RestoreFilesButton").IsEnabled, "Копии не готовы к проверке и восстановлению после обновления состояния агента.");
        window.UpdateLayout();
        if (!Control<Button>("RestoreFilesButton").IsVisible || !Control<Button>("RestoreFilesButton").IsEnabled
            || Control<Button>("ExtractOneCButton").IsVisible) throw new InvalidOperationException(
                $"Каталог не предлагает восстановление файлов: видимость {Control<Button>("RestoreFilesButton").Visibility}, доступ {Control<Button>("RestoreFilesButton").IsEnabled}, видимость извлечения 1С {Control<Button>("ExtractOneCButton").Visibility}.");
        if (!Control<TextBlock>("SelectedCopyPath").Text.Contains("архивов для восстановления: 2")
            || !Control<Button>("ExportCopyButton").Content.ToString()!.Contains("этот архив"))
            throw new InvalidOperationException("Каталог не объясняет зависимость инкремента от цепочки.");
        screenshot("files-incremental-catalog-compact");
        Click("VerifyCopyButton"); await WaitUntil(() => window.SmokeReady, "Проверка файловой цепочки не завершилась.");
        if (!Control<TextBlock>("CopyVerificationStatus").Text.Contains("Файловая цепочка проверена: архивов 2"))
            throw new InvalidOperationException("Ручная проверка не проверила всю файловую цепочку.");
        var exported = Path.Combine(fixture, "exported-increment.zip");
        window.CopyExportDialogOverride = dialog => { dialog.FileName = exported; return true; };
        try
        {
            Click("ExportCopyButton"); await WaitUntil(() => window.SmokeReady, "Экспорт инкремента не завершился.");
            var exportedBytes = await File.ReadAllBytesAsync(exported);
            var originalBytes = await File.ReadAllBytesAsync(latest.Backup.Location);
            if (!Control<TextBlock>("CopyVerificationStatus").Text.Contains("полной основой (архивов: 2)")
                || !exportedBytes.SequenceEqual(originalBytes))
                throw new InvalidOperationException("Экспорт инкремента потерял архив или пояснение о цепочке.");
        }
        finally { window.CopyExportDialogOverride = null; }
        var parent = Path.Combine(fixture, "ordinary-restores"); Directory.CreateDirectory(parent);
        var restoreProgress = new List<FileRestoreProgress>();
        var progressCaptured = false;
        window.FileRestoreProgressObserved = value =>
        {
            restoreProgress.Add(value);
            if (!progressCaptured && value.Phase == FileRestorePhase.VerifyingFiles)
            { progressCaptured = true; screenshot("files-restore-progress-compact"); }
        };
        try { await window.RestoreSelectedFilesAsync(parent); }
        finally { window.FileRestoreProgressObserved = null; }
        if (!Control<TextBlock>("CopyVerificationStatus").Text.Contains("Восстановлено файлов: 3; архивов в цепочке: 2"))
            throw new InvalidOperationException(Control<TextBlock>("CopyVerificationStatus").Text);
        if (!restoreProgress.Any(value => value.Phase == FileRestorePhase.TransferringArchive)
            || !restoreProgress.Any(value => value.Phase == FileRestorePhase.VerifyingFiles)
            || Control<Border>("CopyExportProgressPanel").Visibility != Visibility.Collapsed
            || !Control<TextBlock>("Status").Text.Contains("Файлы восстановлены"))
            throw new InvalidOperationException("Ход или конечный результат восстановления не показан в интерфейсе.");
        var restored = Directory.GetDirectories(parent).Single();
        if (await File.ReadAllTextAsync(Path.Combine(restored, "stable.txt")) != "unchanged"
            || await File.ReadAllTextAsync(Path.Combine(restored, "change.txt")) != "after"
            || await File.ReadAllTextAsync(Path.Combine(restored, "added.txt")) != "added"
            || File.Exists(Path.Combine(restored, "remove.txt")) || !Directory.Exists(Path.Combine(restored, "empty"))
            || beforeCatalog != JsonSerializer.Serialize(await catalog.ReadAsync()) || beforeHistory != JsonSerializer.Serialize(await history.ReadAsync()))
            throw new InvalidOperationException("Восстановленная цепочка или неизменность журнала не подтверждены.");
        screenshot("files-incremental-restored-compact");

        // A verified archive can still omit source files. Exercise the real source sharing
        // failure, manual verification, latest-copy audit, and restore warning together.
        var blocked = Path.Combine(source, "blocked.txt");
        await File.WriteAllTextAsync(blocked, "must be reported as skipped");
        Click("JobsNavigation");
        Control<DataGrid>("Jobs").SelectedItem = Control<DataGrid>("Jobs").Items.OfType<BackupJob>().Single(item => item.Id == job.Id);
        await WaitUntil(() => window.SmokeReady && Control<Button>("RunButton").IsEnabled, "Запуск неполной копии не готов.");
        using (var locked = new FileStream(blocked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Click("RunButton");
            await WaitUntil(() => window.SmokeReady, "Копирование доступных файлов не завершилось.");
        }
        var partial = (await catalog.ReadAsync()).Single(record => record.JobId == job.Id && record.Backup.FileSnapshot?.IsIncomplete == true);
        if (partial.Backup.FileSnapshot!.IsFull || partial.Backup.FileSnapshot.ParentSnapshotId != latest.Backup.FileSnapshot!.SnapshotId)
            throw new InvalidOperationException("Ошибка отдельного файла превратила инкремент в полную копию.");
        await WaitUntil(() => Control<TextBlock>("SelectedJobSummary").Text.Contains("Копия создана, есть предупреждения"),
            "Результат копирования с пропусками не обновился.");
        var summaryBrush = Control<TextBlock>("SelectedJobSummary").Foreground as System.Windows.Media.SolidColorBrush;
        var warningBrush = (System.Windows.Media.SolidColorBrush)window.FindResource("WarningBrush");
        if (summaryBrush?.Color != warningBrush.Color)
            throw new InvalidOperationException("Успешная копия с предупреждениями окрашена как ошибка.");
        if (!File.Exists(first.Backup.Location) || !File.Exists(latest.Backup.Location))
            throw new InvalidOperationException("Неполная копия удалила предыдущую файловую цепочку.");
        Click("CheckLatestCopiesButton");
        await WaitUntil(() => window.SmokeReady, "Проверка последней неполной копии не завершилась.");
        if (!Control<Expander>("StorageCheckResults").IsExpanded
            || !Control<Expander>("StorageCheckResults").Header.ToString()!.Contains("Неполных копий: 1")
            || !Control<TextBox>("StorageCheckDetails").Text.Contains(BackupCopyRow.IncompleteWarning))
            throw new InvalidOperationException("Проверка последних копий скрыла предупреждение о пропусках.");
        screenshot("files-partial-audit-compact");
        Click("CopiesNavigation");
        await WaitUntil(() => window.SmokeReady, "Каталог неполной копии не загрузился.");
        Control<ComboBox>("CopyJobFilter").SelectedValue = job.Id;
        Control<DataGrid>("Copies").SelectedItem = Control<DataGrid>("Copies").Items.OfType<BackupCopyRow>().Single(row => row.Record.Id == partial.Id);
        await WaitUntil(() => window.SmokeReady && Control<Button>("VerifyCopyButton").IsEnabled, "Проверка неполной копии недоступна.");
        if (!Control<TextBlock>("SelectedCopyPath").Text.StartsWith("Неполная копия"))
            throw new InvalidOperationException("Подпись выбранной копии скрывает пропущенные файлы.");
        if (!Control<TextBlock>("SelectedCopyPath").Text.Contains("Инкрементная копия")
            || !Control<TextBlock>("SelectedCopyPath").Text.Contains($"архивов для восстановления: {partial.Backup.FileSnapshot.Depth + 1}"))
            throw new InvalidOperationException("Подпись неполного инкремента скрывает зависимости восстановления.");
        Click("VerifyCopyButton");
        await WaitUntil(() => window.SmokeReady, "Ручная проверка неполной копии не завершилась.");
        var checkedPartial = Control<DataGrid>("Copies").Items.OfType<BackupCopyRow>().Single(row => row.Record.Id == partial.Id);
        if (!Control<TextBlock>("CopyVerificationStatus").Text.Contains(BackupCopyRow.IncompleteWarning)
            || !checkedPartial.CheckSummary.StartsWith("С пропусками") || checkedPartial.CheckColor != "#92400E"
            || !checkedPartial.CheckHelp.Contains(BackupCopyRow.IncompleteWarning))
            throw new InvalidOperationException("Проверка целостности показала неполную копию как полную.");
        var partialParent = Path.Combine(fixture, "partial-restores"); Directory.CreateDirectory(partialParent);
        await window.RestoreSelectedFilesAsync(partialParent);
        var partialDirectory = Directory.GetDirectories(partialParent).Single();
        if (File.Exists(Path.Combine(partialDirectory, "blocked.txt"))
            || await File.ReadAllTextAsync(Path.Combine(partialDirectory, "stable.txt")) != "unchanged"
            || !Control<TextBlock>("CopyVerificationStatus").Text.Contains("blocked.txt")
            || !Control<TextBlock>("CopyVerificationStatus").Text.Contains("исходная копия неполная"))
            throw new InvalidOperationException("Восстановление неполной копии потеряло доступные файлы или предупреждение о пропусках.");
        screenshot("files-partial-restored-compact");
        window.Width = 1100; window.Height = 740;
    }
}
