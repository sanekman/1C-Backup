using System.Windows;
using System.Windows.Controls;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private string? oneCCredentialKey, oneCUsername;
    internal Action<FtpCredentialsWindow>? OneCCredentialsDialogOpened { get; set; }
    private void UpdateOneCCredentialStatus()
    {
        if (OneCCredentialStatus is null) return;
        OneCCredentialStatus.Text = oneCCredentialKey is null
            ? oneCUsername is null ? "Для базы с пользователями задайте пользователя ИБ 1С и пароль." : "Задайте пароль пользователя 1С заново."
            : "Пользователь ИБ: " + oneCUsername + ". Пароль сохранён в Windows.";
    }
    private void OneCCredentials_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Source.Text)) { RejectWizard("Сначала выберите базу 1С.",0,Source); return; }
        var key=Guid.NewGuid().ToString("N");
        var dialog=new FtpCredentialsWindow(key,new WindowsOneCCredentialStore().Save,"Пользователь информационной базы 1С") { Owner=this };
        ((TextBlock)dialog.FindName("BindingName")).Text="База: " + Source.Text.Trim();
        ((TextBlock)dialog.FindName("CredentialExplanation")).Text="Укажите пользователя из списка входа 1С, с правом выгрузки базы. Пароль сохраняется в Windows. Вход Windows для DT отключён.";
        var username=(TextBox)dialog.FindName("Username"); username.Text=oneCUsername ?? "";
        OneCCredentialsDialogOpened?.Invoke(dialog);
        if (dialog.ShowDialog()==true)
        {
            oneCCredentialKey=key; oneCUsername=username.Text.Trim(); UpdateOneCCredentialStatus(); EditorFieldChanged(sender,e);
        }
    }
}
