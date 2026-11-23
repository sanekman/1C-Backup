using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    internal Task ReloadSmokeJobsAsync() =>
        SmokeMode ? LoadEffectiveJobsAsync() : throw new InvalidOperationException();
}

internal static partial class DesktopSmoke
{
    private sealed record PortableFixture(string Kit, string Password, string Increment);
    private const string ExpectedChangedFile = "new version in the increment";
    private const string ExpectedFolderFile = "retained from the full archive";

    private static async Task VerifySplitRestoreAndExportAsync(MainWindow window, BackupCatalog catalog, string lab,
        Action<string, Window> screenshot)
    {
        RequireRecovery(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" && Directory.Exists(lab),
            "Для проверки восстановления нужен изолированный каталог.");
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var store = new JobStore(Path.Combine(DesktopPaths.DataDirectory, "jobs.json"), useLocalTime: true);
        var original = await store.LoadAsync();
        var archive = new WindowsArchivePasswordStore();
        var password = "Only for this fixture " + Guid.NewGuid().ToString("N");
        var key = archive.SaveNew(password);
        string Folder(string name) => Directory.CreateDirectory(Path.Combine(lab, name)).FullName;
        var source = Folder("source");
        var selected = Directory.CreateDirectory(Path.Combine(source, "selected")).FullName;
        await File.WriteAllTextAsync(Path.Combine(source, "change.txt"), "before");
        await File.WriteAllTextAsync(Path.Combine(source, "other.txt"), "outside the selection");
        await File.WriteAllTextAsync(Path.Combine(selected, "inside.txt"), ExpectedFolderFile);
        var a = Folder("a");
        var b = Folder("b");
        var job = new BackupJob
        {
            Name = "Разделённая цепочка", Kind = DatabaseKind.FilesAndFolders, Source = source, DestinationDirectory = a,
            AdditionalDestinations = [new() { DestinationDirectory = b }], FileBackupMode = FileBackupMode.Incremental,
            ScheduleKind = ScheduleKind.Manual, ArchivePasswordKey = key
        };
        try
        {
            await store.SaveAsync([.. original, job]);
            var runner = new LocalBackupRunner(new OneCFileArchiveExecutor(), new BackupCompletion(new FileBackupStorage(), catalog, archive),
                new BackupRunHistory(Path.Combine(lab, "runs.json")), Folder("work"));
            RequireRecovery((await runner.RunAsync(job)).Status == BackupRunStatus.Succeeded, "Не создана полная копия стенда.");
            await File.WriteAllTextAsync(Path.Combine(source, "change.txt"), ExpectedChangedFile);
            RequireRecovery((await runner.RunAsync(job)).Status == BackupRunStatus.Succeeded, "Не создан инкремент стенда.");
            var records = (await catalog.ReadAsync()).Where(item => item.JobId == job.Id).ToArray();
            var point = records.Single(item => Path.GetDirectoryName(item.Backup.Location) == b && !item.Backup.FileSnapshot!.IsFull);
            var moved = Folder("held");
            foreach (var item in records.Where(item => Path.GetDirectoryName(item.Backup.Location) == (item.Backup.FileSnapshot!.IsFull ? b : a)))
                File.Move(item.Backup.Location, Path.Combine(moved, item.Id.ToString("N") + ".zip"));

            await window.ReloadSmokeJobsAsync();
            Control<Button>("CopiesNavigation").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => window.SmokeReady, "Каталог разделённой цепочки не загрузился.");
            Control<ComboBox>("CopyJobFilter").SelectedValue = job.Id;
            Control<ComboBox>("CopyDateFilter").SelectedIndex = 0;
            var copies = Control<DataGrid>("Copies");
            copies.SelectedItem = copies.Items.OfType<BackupCopyRow>().Single(item => item.Record.Id == point.Id);
            var target = Folder("selected-restore");
            var selectionApplied = false;
            window.CopyRestoreDialogOpened = dialog => dialog.Dispatcher.BeginInvoke(() =>
            {
                var consent = (CheckBox)dialog.FindName("AllowOtherDestinations");
                var restore = (Button)dialog.FindName("RestoreButton");
                var choose = (Button)dialog.FindName("ChooseFilesButton");
                dialog.PickFolderOverride = picker => { picker.FolderName = target; return true; };
                ((Button)dialog.FindName("PickFolderButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                RequireRecovery(consent.IsVisible && consent.IsChecked != true && !restore.IsEnabled && !choose.IsEnabled,
                    "Разделённую цепочку можно прочитать без согласия на дополнительные хранилища.");
                screenshot("files-split-consent-compact", dialog);
                consent.IsChecked = true;
                dialog.SelectionDialogOpened = selection => selection.Dispatcher.BeginInvoke(() =>
                {
                    var search = (TextBox)selection.FindName("Search");
                    var entries = (ListBox)selection.FindName("Entries");
                    foreach (var path in new[] { "change.txt", "selected" })
                    {
                        search.Text = path;
                        entries.ScrollIntoView(entries.Items[0]);
                        entries.UpdateLayout();
                        var row = (ListBoxItem)entries.ItemContainerGenerator.ContainerFromIndex(0);
                        FindCheckBox(row).IsChecked = true;
                    }
                    var apply = (Button)selection.FindName("ApplyButton");
                    RequireRecovery(apply.IsEnabled, "Интерфейс не принял выбранный файл и папку.");
                    selectionApplied = true;
                    apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    dialog.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                        () =>
                        {
                            RequireRecovery(dialog.SelectedPaths is { Count: 2 } paths &&
                                paths.Contains("change.txt") && paths.Contains("selected"), "Диалог не сохранил выбор файла и папки.");
                            restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        });
                });
                choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            Control<Button>("RestoreFilesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var status = Control<TextBlock>("CopyVerificationStatus");
            await WaitUntil(() => window.SmokeReady && status.Text.StartsWith("Восстановлено файлов"),
                "Разделённая цепочка не восстановилась через диалог.");
            RequireRecovery(selectionApplied && status.Text.Contains("архивов в цепочке: 2"), "Не подтверждено чтение двух архивов.");
            await VerifyFixtureFilesAsync(Directory.GetDirectories(target).Single(), all: false);

            var kits = Folder("kits");
            window.RecoveryKitExportFolderOverride = picker => { picker.FolderName = kits; return true; };
            var answer = AnswerRecoveryConfirmationAsync(new WindowInteropHelper(window).Handle, "Сохранить комплект", accept: true,
                "дополнительные хранилища");
            OpenRecoveryKitMenuItem(window, "Сохранить комплект выбранной копии…");
            await answer;
            await WaitUntil(() => window.SmokeReady && status.Text.StartsWith("Комплект сохранён"), "Комплект не сохранён через меню.");
            var kit = Directory.GetDirectories(kits).Single();
            var kitSummary = await FileRecoveryKit.InspectAsync(kit);
            RequireRecovery(kitSummary.Encrypted && kitSummary.Archives == 2, "Переносимый комплект потерял шифрование или часть цепочки.");
            Directory.Move(a, Path.Combine(moved, "a"));
            Directory.Move(b, Path.Combine(moved, "b"));
            Directory.Move(source, Path.Combine(moved, "source"));
            await File.WriteAllTextAsync(Path.Combine(lab, "portable-ui.json"),
                JsonSerializer.Serialize(new PortableFixture(kit, password, kitSummary.SnapshotId.ToString("N") + ".zip")));
        }
        finally
        {
            window.RecoveryKitExportFolderOverride = null;
            window.CopyRestoreDialogOpened = null;
            archive.Delete(key);
            foreach (var record in (await catalog.ReadAsync()).Where(item => item.JobId == job.Id))
                await catalog.RemoveAsync(record.Id);
            await store.SaveAsync(original);
            await window.ReloadSmokeJobsAsync();
        }
    }

    private static async Task VerifyPortableKitInFreshProfileAsync(MainWindow window, string lab)
    {
        RequireRecovery(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true", "Нужен новый профиль стенда Windows.");
        var fixture = JsonSerializer.Deserialize<PortableFixture>(await File.ReadAllTextAsync(Path.Combine(lab, "portable-ui.json")))!;
        RequireRecovery((await new JobStore(Path.Combine(DesktopPaths.DataDirectory, "jobs.json")).LoadAsync()).Count == 0
            && (await new BackupCatalog(Path.Combine(DesktopPaths.DataDirectory, "copies.json")).ReadAsync()).Count == 0,
            "Комплект должен открываться в новом профиле без заданий и копий.");
        ((Button)window.FindName("CopiesNavigation")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => window.SmokeReady, "Новое окно не открыло раздел копий.");
        var status = (TextBlock)window.FindName("CopyVerificationStatus");
        window.RecoveryKitOpenFileOverride = picker => { picker.FileName = Path.Combine(fixture.Kit, FileRecoveryKit.ManifestName); return true; };
        async Task Open(string targetName, string password, string expected)
        {
            var target = Directory.CreateDirectory(Path.Combine(lab, targetName)).FullName;
            window.RecoveryKitDialogOpened = dialog => dialog.Dispatcher.BeginInvoke(() =>
            {
                dialog.PickFolderOverride = picker => { picker.FolderName = target; return true; };
                ((Button)dialog.FindName("PickFolder")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var field = (PasswordBox)dialog.FindName("Password");
                RequireRecovery(field.IsVisible, "Зашифрованный комплект не запросил пароль.");
                field.Password = password;
                ((Button)dialog.FindName("Restore")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            OpenRecoveryKitMenuItem(window, "Открыть комплект…");
            await WaitUntil(() => window.SmokeReady && status.Text.Contains(expected), "Открытие комплекта не показало ожидаемый итог.");
            if (expected.StartsWith("Из комплекта"))
                await VerifyFixtureFilesAsync(Directory.GetDirectories(target).Single(), all: true);
            else RequireRecovery(!Directory.EnumerateFileSystemEntries(target).Any(), "Неверный пароль оставил результат.");
        }
        await Open("wrong-password", "Wrong fixture password", "Не удалось восстановить комплект");
        await Open("valid-password", fixture.Password, "Из комплекта восстановлено файлов: 3");
        var increment = Path.Combine(fixture.Kit, fixture.Increment);
        File.Move(increment, increment + ".held");
        try
        {
            window.RecoveryKitDialogOpened = dialog => dialog.Dispatcher.BeginInvoke(() => dialog.DialogResult = false);
            OpenRecoveryKitMenuItem(window, "Открыть комплект…");
            await WaitUntil(() => window.SmokeReady && status.Text.Contains("В комплекте отсутствует архив"),
                "Не показан отсутствующий инкремент.");
        }
        finally { File.Move(increment + ".held", increment); }
    }

    private static void OpenRecoveryKitMenuItem(MainWindow window, string header)
    {
        window.RecoveryKitMenuOpened = menu => menu.Dispatcher.BeginInvoke(() =>
        {
            var item = menu.Items.OfType<MenuItem>().Single(entry => Equals(entry.Header, header));
            menu.IsOpen = false;
            RequireRecovery(item.IsEnabled, "Пункт меню комплекта недоступен.");
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        });
        ((Button)window.FindName("RecoveryKitsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.RecoveryKitMenuOpened = null;
    }

    private static CheckBox FindCheckBox(DependencyObject row)
    {
        if (row is CheckBox check) return check;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(row); index++)
            if (FindCheckBoxOrNull(VisualTreeHelper.GetChild(row, index)) is { } found) return found;
        throw new InvalidOperationException("В строке копии нет флажка выбора.");

        static CheckBox? FindCheckBoxOrNull(DependencyObject node)
        {
            if (node is CheckBox check) return check;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
                if (FindCheckBoxOrNull(VisualTreeHelper.GetChild(node, index)) is { } found) return found;
            return null;
        }
    }

    private static async Task VerifyFixtureFilesAsync(string directory, bool all)
    {
        RequireRecovery(await File.ReadAllTextAsync(Path.Combine(directory, "change.txt")) == ExpectedChangedFile &&
            await File.ReadAllTextAsync(Path.Combine(directory, "selected", "inside.txt")) == ExpectedFolderFile &&
            File.Exists(Path.Combine(directory, "other.txt")) == all, "Содержимое восстановленных файлов отличается от ожидаемого.");
    }
}
