using System.Windows;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class ArchivePasswordWindow : Window
{
    private readonly bool restoring;
    public string? NewKey { get; private set; }
    public string? PasswordOverride { get; private set; }
    public ArchivePasswordWindow(bool restoring = false, string restoreAction = "Извлечь копию")
    {
        this.restoring = restoring;
        InitializeComponent();
        Instructions.Text = restoring
            ? "Введите пароль выбранной копии. Оставьте поле пустым, чтобы использовать сохранённый пароль. Введённый пароль действует только для этой операции."
            : "После сохранения задания этот пароль будет использоваться для новых копий. Для прежних архивов действует их прежний пароль.";
        if (restoring) { ConfirmationPanel.Visibility = Visibility.Collapsed; ApplyButton.Content = restoreAction; }
        Closed += (_, _) => { Password.Clear(); Confirmation.Clear(); };
    }
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (restoring) PasswordOverride = Password.Password.Length == 0 ? null : Password.Password;
            else
            {
                if (Password.Password != Confirmation.Password) throw new ArgumentException("Пароли не совпадают.");
                NewKey = new WindowsArchivePasswordStore().SaveNew(Password.Password);
            }
            Password.Clear(); Confirmation.Clear(); DialogResult = true;
        }
        catch (ArgumentException ex) { Status.Text = ex.Message; }
        catch (Exception) { Status.Text = "Не удалось сохранить пароль архива в Windows. Повторите попытку."; }
    }
}
