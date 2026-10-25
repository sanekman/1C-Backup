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
            ? "Введите пароль этой копии. Пустое поле — использовать сохранённый пароль Windows. Введённый пароль не сохраняется."
            : "Новый пароль сохранится для текущего пользователя Windows. Сохраните задание после закрытия окна. Прежние копии сохраняют свой пароль.";
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
        catch (Exception ex) { Status.Text = ex.Message; }
    }
}
