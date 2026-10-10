using System.Windows;

using OneCBackup.Core;

namespace OneCBackup.Desktop;

public partial class FtpCredentialsWindow : Window
{
    private readonly string key;
    private readonly WindowsFtpCredentialStore store;
    public FtpCredentialsWindow(string key, WindowsFtpCredentialStore store)
    {
        this.key = key; this.store = store;
        InitializeComponent(); BindingName.Text = "Идентификатор: " + key;
        Closed += (_, _) => Password.Clear();
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            store.Save(key, Username.Text.Trim(), Password.Password);
            Password.Clear(); DialogResult = true;
        }
        catch (Exception ex) { Status.Text = "Не удалось сохранить: " + ex.Message; }
    }
}
