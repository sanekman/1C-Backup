using System.Windows;

using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class FtpCredentialsWindow : Window
{
    private readonly string key;
    internal string CredentialKey => key;
    private readonly Action<string, string, string> save;
    private bool storageDraft;
    public FtpCredentialsWindow(string key, WindowsFtpCredentialStore store)
        : this(key, store.Save, "Логин и пароль FTP")
    {
    }

    internal void DescribeStorageDraft(string address, string? username)
    {
        storageDraft = true;
        BindingName.Text = address;
        Username.Text = username ?? "";
        CredentialExplanation.Text = "Пароль хранится в Windows. Новый доступ применяется после сохранения задания. Затем проверьте хранилище.";
        EmptyPassword.Visibility = Visibility.Visible;
        CredentialSaveButton.Content = "Применить к черновику";
        Loaded += (_, _) => { if (!string.IsNullOrEmpty(Username.Text)) Password.Focus(); };
    }

    public FtpCredentialsWindow(string key, Action<string, string, string> save, string title)
    {
        this.key = key; this.save = save;
        InitializeComponent(); Title = title; BindingName.Text = "Идентификатор: " + key;
        Closed += (_, _) => Password.Clear();
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var password = Password.Password;
            if (storageDraft)
            {
                var useEmptyPassword = EmptyPassword.IsChecked == true;
                if (password.Length == 0 && !useEmptyPassword)
                {
                    Status.Text = "Введите пароль или выберите «Использовать пустой пароль».";
                    Password.Focus();
                    return;
                }
                if (password.Length > 0 && useEmptyPassword)
                {
                    Status.Text = "Снимите флажок «Использовать пустой пароль» или очистите поле пароля.";
                    EmptyPassword.Focus();
                    return;
                }
            }
            save(key, Username.Text.Trim(), password);
            Password.Clear(); DialogResult = true;
        }
        catch (ArgumentException ex) { Status.Text = ex.Message; }
        catch (Exception) { Status.Text = "Не удалось сохранить доступ в Windows. Повторите попытку или проверьте права пользователя."; }
    }
}
