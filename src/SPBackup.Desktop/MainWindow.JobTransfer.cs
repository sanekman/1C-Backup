using System.IO;
using System.Windows;
using Microsoft.Win32;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private bool jobFileTransfer;
    internal Func<SaveFileDialog, bool?>? JobExportDialogOverride { get; set; }
    internal Func<OpenFileDialog, bool?>? JobImportDialogOverride { get; set; }

    private async void ExportJobSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!ExportJobSettingsMenuItem.IsEnabled || Jobs.SelectedItem is not BackupJob job) return;
        var dialog = new SaveFileDialog { Title = "Сохранить настройки задания", FileName = "backup-job.json",
            DefaultExt = ".json", Filter = "Настройки задания (*.json)|*.json", AddExtension = true, OverwritePrompt = false, CheckPathExists = true };
        if ((JobExportDialogOverride is null ? dialog.ShowDialog(this) : JobExportDialogOverride(dialog)) != true) return;
        jobFileTransfer = true; UpdateRunControls();
        Status.Text = "Сохраняются настройки задания…";
        try
        {
            var profile = Path.GetFullPath(DesktopPaths.DataDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(dialog.FileName).StartsWith(profile, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Выберите папку вне рабочего профиля приложения.");
            await Task.Run(() => BackupJobTransfer.ExportAsync(job, dialog.FileName));
            Status.Text = "Настройки сохранены: " + dialog.FileName + ". Значения паролей в файл не записываются; на другом компьютере задайте их заново.";
        }
        catch (Exception ex) { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex); Status.Text = "Не удалось сохранить настройки задания: " + ex.Message; }
        finally { jobFileTransfer = false; UpdateRunControls(); }
    }

    private async void ImportJobSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!ImportJobSettingsMenuItem.IsEnabled) return;
        var dialog = new OpenFileDialog { Title = "Загрузить настройки задания", Filter = "Настройки задания (*.json)|*.json", CheckFileExists = true };
        if ((JobImportDialogOverride is null ? dialog.ShowDialog(this) : JobImportDialogOverride(dialog)) != true) return;
        jobFileTransfer = true; UpdateRunControls();
        Status.Text = "Читаются настройки задания…";
        try
        {
            var imported = await Task.Run(() => BackupJobTransfer.ImportAsync(dialog.FileName));
            var requiresPostgreSqlPassword = imported.Kind == DatabaseKind.PostgreSql && imported.PostgreSql?.CredentialKey is not null;
            if (requiresPostgreSqlPassword && imported.PostgreSql is { } pg)
            {
                var known = jobs.Any(local => local.Kind == DatabaseKind.PostgreSql && local.PostgreSql is { } trusted
                    && trusted.CredentialKey == pg.CredentialKey && trusted.Port == pg.Port
                    && string.Equals(trusted.Host.Trim(), pg.Host.Trim(), StringComparison.OrdinalIgnoreCase)
                    && string.Equals(trusted.Username, pg.Username, StringComparison.Ordinal));
                var available = false;
                if (known)
                {
                    try { var credential = await new WindowsPostgreSqlCredentialStore().GetAsync(pg.CredentialKey!);
                        available = credential.UserName == pg.Username; }
                    catch { /* Ask for a new password instead of using an unavailable binding. */ }
                }
                if (!available) imported = imported with { PostgreSql = pg with { CredentialKey = null } };
            }
            var passwordUnavailable = false;
            if (imported.ArchivePasswordKey is not null)
            {
                try { await new WindowsArchivePasswordStore().GetAsync(imported.ArchivePasswordKey); }
                catch { passwordUnavailable = true; }
            }
            if (imported.Kind == DatabaseKind.WebDav && imported.WebDav is not null)
            {
                try { await new WindowsWebDavCredentialStore().GetAsync(imported.WebDav.CredentialKey); }
                catch { imported = imported with { WebDav = imported.WebDav with { CredentialKey = "" } }; }
            }
            if (imported.SqlServer is { Authentication: SqlServerAuthentication.SqlServer } sql)
            {
                // An imported reference is not permission to send an existing secret to a new server.
                var knownServer = jobs.Any(local => local.Kind == DatabaseKind.SqlServer
                    && local.SqlServer is { Authentication: SqlServerAuthentication.SqlServer } trusted
                    && trusted.CredentialKey == sql.CredentialKey
                    && string.Equals(trusted.Server.Trim(), sql.Server.Trim(), StringComparison.OrdinalIgnoreCase));
                var available = false;
                if (knownServer)
                {
                    try { await new WindowsSqlServerCredentialStore().GetAsync(sql.CredentialKey!); available = true; }
                    catch { /* Enter the password again instead of accepting an unavailable binding. */ }
                }
                if (!available) imported = imported with { SqlServer = sql with { CredentialKey = null } };
            }
            selectionBeforeEdit = Jobs.SelectedItem as BackupJob;
            LoadJobEditor(imported);
            if (requiresPostgreSqlPassword) PgAuthenticationPicker.SelectedIndex = 0;
            editingId = null;
            var name = imported.Name + " — импорт";
            var suffix = 2;
            while (jobs.Any(job => string.Equals(job.Name, name, StringComparison.OrdinalIgnoreCase)))
                name = imported.Name + " — импорт " + suffix++;
            JobName.Text = name; Enabled.IsChecked = false;
            if (passwordUnavailable)
            {
                archivePasswordKey = null;
                EncryptArchive.IsChecked = true;
                UpdateArchivePasswordStatus();
            }
            OpenEditor();
            WizardHint.Text = "Настройки загружены в новое задание и ещё не сохранены. Проверьте пути и учётные данные. Автоматические запуски отключены."
                + (passwordUnavailable ? " Пароль архива недоступен: задайте его заново на шаге «Хранилища» или явно выключите шифрование." : "");
            Status.Text = "Проверьте импортированное задание в мастере. Изменения ещё не сохранены.";
        }
        catch (Exception ex) { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex); Status.Text = "Не удалось загрузить настройки задания: " + ex.Message; }
        finally { jobFileTransfer = false; UpdateRunControls(); }
    }
}
