using System.Windows;
using Microsoft.Win32;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class RecoveryKitWindow : Window
{
    private readonly bool encrypted;

    internal RecoveryKitWindow(FileRecoveryKitSummary summary)
    {
        InitializeComponent();
        encrypted = summary.Encrypted;
        PointInfo.Text = $"Копия от {summary.CreatedUtc.ToLocalTime():dd.MM.yyyy HH:mm} · архивов: {summary.Archives}"
            + (summary.Incomplete ? "\nКопия с пропусками. Для некоторых файлов доступна прежняя версия." : "");
        PasswordFields.Visibility = encrypted ? Visibility.Visible : Visibility.Collapsed;
        Closed += (_, _) => Password.Clear();
    }

    internal string? RestoreParent { get; private set; }
    internal string? ArchivePassword { get; private set; }

    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Выберите папку для восстановления" };
        if (picker.ShowDialog(this) != true) return;
        RestoreParent = Destination.Text = picker.FolderName;
        Restore.IsEnabled = true;
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (RestoreParent is null) return;
        try { if (encrypted) ArchivePasswords.Validate(Password.Password); }
        catch (ArgumentException problem) { Error.Text = problem.Message; Password.Focus(); return; }
        ArchivePassword = encrypted ? Password.Password : null;
        DialogResult = true;
    }
}
