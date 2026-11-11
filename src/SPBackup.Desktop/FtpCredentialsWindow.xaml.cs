using System.Windows;

using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class FtpCredentialsWindow : Window
{
    private readonly string key;
    internal string CredentialKey => key;
    private readonly Action<string, string, string> save;
    public FtpCredentialsWindow(string key, WindowsFtpCredentialStore store)
        : this(key, store.Save, "Логин и пароль FTP")
    {
        SizeToContent = SizeToContent.Height;
        BindingName.Text = "Доступ к FTP / FTPS для этого хранилища.";
        CredentialExplanation.Text = "Новый доступ сохраняется в Windows отдельно от прежнего. Он применяется после сохранения задания или хранилища. Отмена редактирования не меняет доступ уже сохранённых заданий.";
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
            save(key, Username.Text.Trim(), Password.Password);
            Password.Clear(); DialogResult = true;
        }
        catch (Exception ex) { Status.Text = "Не удалось сохранить: " + ex.Message; }
    }
}
