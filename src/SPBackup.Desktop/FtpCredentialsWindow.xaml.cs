using System.Windows;

using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class FtpCredentialsWindow : Window
{
    private readonly string key;
    internal string CredentialKey => key;
    private readonly Action<string, string, string> save;
    private bool jobDraft;
    public FtpCredentialsWindow(string key, WindowsFtpCredentialStore store)
        : this(key, store.Save, "Логин и пароль FTP")
    {
    }

    internal void DescribeStorageDraft(string address, string? username)
        => DescribeJobDraft(address, username, "Логин",
            "Пароль хранится в Windows. Новый доступ применяется после сохранения задания. Затем проверьте хранилище.");

    internal void DescribeJobDraft(string address, string? username, string loginLabel, string explanation)
    {
        jobDraft = true;
        BindingName.Text = address;
        Username.Text = username ?? "";
        UsernameLabel.Text = loginLabel;
        System.Windows.Automation.AutomationProperties.SetName(Username, loginLabel);
        CredentialExplanation.Text = explanation;
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
            if (jobDraft)
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
