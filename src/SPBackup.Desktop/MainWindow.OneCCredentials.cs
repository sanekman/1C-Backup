using System.IO;
using System.Windows;
using System.Windows.Controls;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private string? oneCCredentialKey, oneCUsername, oneCCredentialSource;
    private bool oneCCredentialsChanged;
    private string? OneCCredentialForSource => string.Equals(oneCCredentialSource, Source.Text.Trim(), StringComparison.OrdinalIgnoreCase)
        ? oneCCredentialKey : null;
    internal Action<FtpCredentialsWindow>? OneCCredentialsDialogOpened { get; set; }

    private void UpdateOneCCredentialStatus()
    {
        if (OneCCredentialStatus is null || Source is null) return;
        var available = OneCCredentialForSource is not null;
        OneCCredentialsButton.Content = available ? "Изменить доступ к базе 1С…" : "Задать пользователя и пароль 1С…";
        OneCCredentialStatus.Text = available
            ? $"Пользователь информационной базы: {oneCUsername}. "
                + (oneCCredentialsChanged ? "Доступ изменён в черновике." : "Пароль задан. Вход подтверждается выгрузкой DT.")
            : oneCUsername is not null ? "Задайте пользователя и пароль для этой базы заново."
            : "Доступ не задан. Для базы без пользователей реквизиты не требуются.";
    }

    private void OneCCredentials_Click(object sender, RoutedEventArgs e)
    {
        var source = Source.Text.Trim();
        if (!Path.IsPathFullyQualified(source))
        {
            RejectWizard("Сначала укажите полный путь к базе 1С.", 0, Source);
            return;
        }
        var key = Guid.NewGuid().ToString("N");
        var dialog = new FtpCredentialsWindow(key, new WindowsOneCCredentialStore().Save, "Доступ к информационной базе 1С") { Owner = this };
        dialog.DescribeJobDraft("База 1С: " + source, OneCCredentialForSource is null ? null : oneCUsername, "Пользователь информационной базы",
            "Укажите пользователя 1С с правом выгрузки. Доступ применяется после сохранения задания. Вход подтверждается выгрузкой DT.");
        OneCCredentialsDialogOpened?.Invoke(dialog);
        if (dialog.ShowDialog() == true)
        {
            oneCCredentialKey = key;
            oneCCredentialSource = source;
            oneCUsername = ((TextBox)dialog.FindName("Username")).Text.Trim();
            oneCCredentialsChanged = true;
            UpdateOneCCredentialStatus();
            EditorFieldChanged(sender, e);
        }
    }
}
