using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using SPBackup.Core;

namespace SPBackup.Desktop;

internal static partial class DesktopSmoke
{
    private static async Task VerifyFormerUncAsync(MainWindow window, BackupJob current, BackupCatalog catalog,
        string formerUnc, Action<string> screenshot, Action<string, Window> screenshotDialog)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" || !formerUnc.StartsWith(@"\\", StringComparison.Ordinal)
            || !NetworkShareSettings.Root(formerUnc).Equals(formerUnc, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Нужен отдельный UNC-ресурс стенда Windows.");
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var fixture = Path.Combine(Path.GetDirectoryName(DesktopPaths.DataDirectory)!, "former-unc");
        Directory.CreateDirectory(fixture);
        var target = Directory.CreateDirectory(Path.Combine(fixture, "restored")).FullName;
        var previous = current with
        {
            StorageKind = StorageKind.NetworkShare, DestinationDirectory = formerUnc, AdditionalDestinations = null,
            NetworkShare = null, FileBackupMode = FileBackupMode.Full, TemporaryDirectory = null
        };
        var storage = new FileBackupStorage();
        var run = await new LocalBackupRunner(new OneCFileArchiveExecutor(), new BackupCompletion(storage, catalog),
            new BackupRunHistory(Path.Combine(fixture, "runs.json")), Path.Combine(fixture, "work")).RunAsync(previous);
        Require(run.Status == BackupRunStatus.Succeeded, "Не удалось создать архив на тестовом SMB-ресурсе.");
        var point = (await catalog.ReadAsync()).Single(record => record.JobId == current.Id && record.Backup == run.Backup);
        var original = SHA256.HashData(await File.ReadAllBytesAsync(point.Backup.Location));
        var profilePath = Path.Combine(DesktopPaths.DataDirectory, "jobs.json");
        var saved = await File.ReadAllBytesAsync(profilePath);
        var grid = Control<DataGrid>("Copies");
        try
        {
            Control<Button>("RefreshCopiesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => window.SmokeReady, "Каталог не загрузил копию из прежнего хранилища.");
            grid.SelectedItem = grid.Items.OfType<BackupCopyRow>().Single(row => row.Record.Id == point.Id);
            var restore = Control<Button>("RestoreFilesButton");
            await WaitUntil(() => restore.IsEnabled && restore.IsVisible, "Восстановление прежней копии недоступно с панели.");
            var opened = false;
            window.CopyRestoreDialogOpened = dialog => dialog.Dispatcher.BeginInvoke(() =>
            {
                opened = true;
                dialog.DialogResult = false;
            });
            var owner = new WindowInteropHelper(window).Handle;
            var decline = AnswerFormerUncConfirmationAsync(owner, formerUnc, accept: false);
            restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await decline;
            Require(!opened && !Directory.EnumerateFileSystemEntries(target).Any(),
                "Отмена подтверждения открыла восстановление или создала файлы.");

            window.CopyRestoreDialogOpened = dialog => dialog.Dispatcher.BeginInvoke(() =>
            {
                opened = true;
                dialog.PickFolderOverride = picker => { picker.FolderName = target; return true; };
                ((Button)dialog.FindName("PickFolderButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var apply = (Button)dialog.FindName("RestoreButton");
                Require(apply.IsEnabled, "Диалог не принял папку восстановления для прежнего хранилища.");
                dialog.UpdateLayout();
                screenshotDialog("files-former-unc-confirm-compact", dialog);
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            var accept = AnswerFormerUncConfirmationAsync(owner, formerUnc, accept: true);
            restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await accept;
            var status = Control<TextBlock>("CopyVerificationStatus");
            await WaitUntil(() => opened && window.SmokeReady && status.Text.StartsWith("Восстановлено файлов"),
                "Восстановление через окно прежнего хранилища не завершилось.");
            var destination = Directory.GetDirectories(target).Single();
            Require(await File.ReadAllTextAsync(Path.Combine(destination, "stable.txt")) == "unchanged"
                && SHA256.HashData(await File.ReadAllBytesAsync(point.Backup.Location)).SequenceEqual(original)
                && (await File.ReadAllBytesAsync(profilePath)).SequenceEqual(saved),
                "Восстановление изменило архив, профиль или содержимое файла.");
            screenshot("files-former-unc-restored-compact");
        }
        finally
        {
            window.CopyRestoreDialogOpened = null;
            await catalog.RemoveAsync(point.Id);
            Control<Button>("RefreshCopiesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => window.SmokeReady, "Каталог не обновился после восстановления.");
        }
    }

    private static Task AnswerFormerUncConfirmationAsync(nint owner, string path, bool accept)
    {
        var thread = CertificateGetWindowThreadProcessId(owner, out _);
        return Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                nint dialog = 0;
                CertificateEnumThreadWindows(thread, (handle, _) =>
                {
                    var title = new StringBuilder(256);
                    CertificateGetWindowText(handle, title, title.Capacity);
                    if (CertificateGetWindow(handle, 4) == owner && title.ToString() == "Открыть прежнее хранилище")
                        dialog = handle;
                    return true;
                }, 0);
                if (dialog != 0)
                {
                    var body = new StringBuilder();
                    EnumChildWindows(dialog, (handle, _) =>
                    {
                        var value = new StringBuilder(1024);
                        CertificateGetWindowText(handle, value, value.Capacity);
                        body.AppendLine(value.ToString());
                        return true;
                    }, 0);
                    var message = body.ToString();
                    var valid = message.Contains(path, StringComparison.Ordinal)
                        && message.Contains("текущего пользователя Windows", StringComparison.Ordinal);
                    var button = CertificateGetDlgItem(dialog, accept && valid ? 1 : 2);
                    Require(button != 0 && CertificatePostMessage(button, 0x00F5, 0, 0), "Не удалось закрыть подтверждение прежнего UNC.");
                    Require(valid, "Подтверждение не показывает прежний путь и пользователя Windows.");
                    return;
                }
                await Task.Delay(20);
            }
            throw new TimeoutException("Окно подтверждения прежнего UNC не открылось.");
        });
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(nint parent, CertificateWindowCallback callback, nint parameter);
}
