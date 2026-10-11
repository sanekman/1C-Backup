using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

public partial class DestinationWindow : Window
{
    private readonly WindowsFtpCredentialStore credentials;
    private string? credentialKey;
    public BackupDestination? Result { get; private set; }

    public DestinationWindow(WindowsFtpCredentialStore credentials, BackupDestination? initial = null)
    {
        InitializeComponent();
        AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, _) => Status.Text = ""));
        AddHandler(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,
            new SelectionChangedEventHandler((_, _) => Status.Text = ""));
        this.credentials = credentials;
        Storage.ItemsSource = new Dictionary<StorageKind, string>
        {
            [StorageKind.LocalDirectory] = "Локальный диск", [StorageKind.NetworkShare] = "Сетевая папка (UNC)",
            [StorageKind.Ftp] = "FTP / FTPS"
        };
        credentialKey = initial?.Ftp?.CredentialKey;
        Destination.Text = initial?.DestinationDirectory ?? "";
        FtpHost.Text = initial?.Ftp?.Host ?? "";
        FtpPort.Text = (initial?.Ftp?.Port ?? 21).ToString();
        FtpTls.IsChecked = initial?.Ftp?.UseTls ?? true;
        Storage.SelectedValue = initial?.StorageKind ?? StorageKind.LocalDirectory;
        CredentialsStatus.Text = credentialKey is null ? "Доступ пока не задан. Пароль сохраняется в Windows." : "Для хранилища задан доступ в Windows.";
        if (initial is not null) SaveButton.Content = "Применить";
    }

    private void Storage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (FtpPanel is null || BrowseButton is null || PathHint is null) return;
        var kind = Storage.SelectedValue is StorageKind selected ? selected : StorageKind.LocalDirectory;
        Height = kind == StorageKind.Ftp ? 540 : 340;
        FtpPanel.Visibility = kind == StorageKind.Ftp ? Visibility.Visible : Visibility.Collapsed;
        BrowseButton.Visibility = kind == StorageKind.Ftp ? Visibility.Collapsed : Visibility.Visible;
        PathHint.Text = kind switch
        {
            StorageKind.NetworkShare => @"Укажите сетевую папку: \\server\share\backups. Доступ — под текущим пользователем Windows.",
            StorageKind.Ftp => "Укажите папку на FTP-сервере, например /backups. FTPS проверяет сертификат сервера.",
            _ => @"Укажите полный путь, например D:\Backups. Папка будет создана при копировании."
        };
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Выберите папку для дополнительных копий" };
        if (dialog.ShowDialog(this) == true) Destination.Text = dialog.FolderName;
    }

    private void Credentials_Click(object sender, RoutedEventArgs e)
    {
        var key = credentialKey ?? "ftp-" + Guid.NewGuid().ToString("N");
        if (new FtpCredentialsWindow(key, credentials) { Owner = this }.ShowDialog() != true) return;
        credentialKey = key;
        Status.Text = "";
        CredentialsStatus.Text = "Логин и пароль сохранены в Windows. Соединение проверяется при доставке копии.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var kind = (StorageKind)Storage.SelectedValue;
        var port = 21;
        if (kind == StorageKind.Ftp && !int.TryParse(FtpPort.Text, out port))
        { Status.Text = "Порт FTP должен быть числом от 1 до 65535."; FtpPort.Focus(); return; }
        var destination = new BackupDestination { StorageKind = kind, DestinationDirectory = Destination.Text.Trim(),
            Ftp = kind == StorageKind.Ftp ? new FtpSettings { Host = FtpHost.Text.Trim(), Port = port,
                UseTls = FtpTls.IsChecked == true, CredentialKey = credentialKey ?? "" } : null };
        var errors = destination.ApplyTo(new BackupJob { Name = "Хранилище", Source = "check" }).Validate().ToList();
        if (kind != StorageKind.Ftp && !Path.IsPathFullyQualified(destination.DestinationDirectory))
            errors.Add("Укажите полный путь к папке копий.");
        if (errors.Count != 0) { Status.Text = errors[0]; return; }
        Result = destination;
        DialogResult = true;
    }
}
