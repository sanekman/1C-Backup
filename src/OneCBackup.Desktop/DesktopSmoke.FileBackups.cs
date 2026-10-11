using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

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
        foreach (var name in new[] { "FileBackupModePicker", "FullBackupEvery" })
        {
            var element = Control<FrameworkElement>(name); var scroll = Control<ScrollViewer>("EditorScroll");
            var point = element.TranslatePoint(new Point(), scroll);
            if (!element.IsVisible || point.Y < 0 || point.Y + element.ActualHeight > scroll.ActualHeight + 1)
                throw new InvalidOperationException("Файловый режим не виден без прокрутки в компактном мастере: " + name);
        }
        screenshot("files-incremental-source-compact");
        Click("NextStepButton");
        Control<TextBox>("Destination").Text = Path.Combine(fixture, "ordinary-copies");
        Control<TextBox>("TemporaryFolder").Text = Path.Combine(fixture, "ordinary-HDD-work");
        Click("NextStepButton");
        Control<TextBox>("KeepLast").Text = "1"; Control<CheckBox>("Enabled").IsChecked = false;
        Click("NextStepButton");
        if (!Control<TextBox>("WizardSummary").Text.Contains("Инкремент") || !Control<TextBox>("WizardSummary").Text.Contains("3"))
            throw new InvalidOperationException("Итог мастера потерял файловый режим и длину цепочки.");
        screenshot("files-incremental-review-compact");
        Click("SaveButton");
        await WaitUntil(() => window.SmokeReady && Control<Border>("JobListCard").IsVisible, "Файловое задание не сохранено.");
        var store = new JobStore(Path.Combine(DesktopPaths.DataDirectory, "jobs.json"));
        var job = (await store.LoadAsync()).Single(job => job.Name == "Документы — инкрементная копия");
        if (job.Kind != DatabaseKind.FilesAndFolders || job.FileBackupMode != FileBackupMode.Incremental || job.FullBackupEvery != 3)
            throw new InvalidOperationException("Файловые настройки потеряны при сохранении.");
        Control<DataGrid>("Jobs").SelectedItem = Control<DataGrid>("Jobs").Items.OfType<BackupJob>().Single(item => item.Id == job.Id);
        var catalog = new BackupCatalog(Path.Combine(DesktopPaths.DataDirectory, "copies.json"));
        var history = new BackupRunHistory(Path.Combine(DesktopPaths.DataDirectory, "runs.json"));
        await WaitUntil(() => window.SmokeReady && Control<Button>("RunButton").IsEnabled, "Ручной запуск файлов не готов после остановки агента.");
        Click("RunButton"); await WaitUntil(() => window.SmokeReady, "Полная файловая копия не завершилась.");
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
        window.Width = 1100; window.Height = 740;
    }
}
