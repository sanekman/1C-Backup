using System.Windows;
using Microsoft.Win32;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class RestoreCopyWindow : Window
{
    internal RestoreCopyWindow(BackupCopyRow copy, bool files, int archives)
    {
        InitializeComponent();
        CopyName.Text = $"{copy.JobName} · {copy.CompletedUtc.ToLocalTime():dd.MM.yyyy HH:mm}";
        CopySource.Text = copy.DisplayLocation;
        ChainStatus.Text = files
            ? $"В каталоге найдены все архивы цепочки ({archives}). При восстановлении программа проверит каждый архив."
                + (copy.Record.Backup.FileSnapshot?.IsIncomplete == true ? "\nКопия с пропусками. Могут восстановиться прежние версии файлов." : "")
            : "Из ZIP будет извлечён файл 1Cv8.1CD. Перед извлечением программа проверит архив.";
        FolderHint.Text = files ? "В выбранной папке будет создан новый подкаталог Restored-files-…"
            : "В выбранной папке будет создан новый подкаталог 1C-Restored-…";
        var encrypted = copy.Record.Backup.ArchivePasswordKey is not null;
        PasswordHint.Text = encrypted
            ? "Пароль архива из Windows будет использован автоматически."
            : "Пароль архива не требуется по данным каталога.";
        AlternatePassword.Visibility = encrypted ? Visibility.Visible : Visibility.Collapsed;
        RestoreButton.Content = files ? "Восстановить файлы" : "Извлечь файл базы";
        Closed += (_, _) => OverridePassword.Clear();
    }

    internal string? ParentDirectory { get; private set; }
    internal string? PasswordOverride { get; private set; }
    internal Func<OpenFolderDialog, bool>? PickFolderOverride { get; set; }

    private void PickFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Выберите папку назначения для восстановления" };
        if (!(PickFolderOverride?.Invoke(dialog) ?? (dialog.ShowDialog(this) == true))) return;
        ParentDirectory = TargetFolder.Text = dialog.FolderName;
        RestoreButton.IsEnabled = true;
    }

    private void AlternatePassword_Expanded(object sender, RoutedEventArgs e) => OverridePassword.Focus();

    private void AlternatePassword_Collapsed(object sender, RoutedEventArgs e)
    {
        OverridePassword.Clear();
        PasswordError.Text = "";
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (ParentDirectory is null) return;
        if (AlternatePassword.IsExpanded)
        {
            try { ArchivePasswords.Validate(OverridePassword.Password); }
            catch (ArgumentException error)
            {
                PasswordError.Text = error.Message;
                OverridePassword.Focus();
                return;
            }
            PasswordOverride = OverridePassword.Password;
        }
        DialogResult = true;
    }
}
