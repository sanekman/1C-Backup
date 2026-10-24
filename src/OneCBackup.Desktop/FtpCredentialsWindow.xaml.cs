using System.Windows;

using OneCBackup.Core;

namespace OneCBackup.Desktop;

public partial class FtpCredentialsWindow : Window
{
    private readonly string key;
    internal string CredentialKey => key;
    private readonly Action<string, string, string> save;
    public FtpCredentialsWindow(string key, WindowsFtpCredentialStore store)
        : this(key, store.Save, "Учётные данные FTP") { }
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
