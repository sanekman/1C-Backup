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
    private GoogleDriveSettings? googleDriveSettings;
    private string? googleDriveFolder;
    private RemoteGatewaySettings? remoteGateway;
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
            [StorageKind.Ftp] = "FTP / FTPS", [StorageKind.GoogleDrive] = "Google Drive", [StorageKind.RemoteGateway] = "Шлюз хранилища (Proxmox)"
        };
        credentialKey = initial?.Ftp?.CredentialKey;
        googleDriveSettings = initial?.GoogleDrive;
        remoteGateway = initial?.RemoteGateway;
        googleDriveFolder = initial?.StorageKind == StorageKind.GoogleDrive ? initial.DestinationDirectory : null;
        Destination.Text = initial?.DestinationDirectory ?? "";
        KeepLast.Text = initial?.KeepLast?.ToString() ?? "";
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
        Height = kind == StorageKind.Ftp ? 620 : 420;
        var cloud = kind == StorageKind.GoogleDrive;
        if (cloud && googleDriveFolder is not null) Destination.Text = googleDriveFolder;
        else if (!cloud && googleDriveFolder is not null && Destination.Text == googleDriveFolder) Destination.Clear();
        GoogleDrivePanel.Visibility = cloud ? Visibility.Visible : Visibility.Collapsed;
        DestinationLabel.Visibility = DestinationPanel.Visibility = PathHint.Visibility = cloud || kind == StorageKind.RemoteGateway ? Visibility.Collapsed : Visibility.Visible;
        RemotePanel.Visibility = kind == StorageKind.RemoteGateway ? Visibility.Visible : Visibility.Collapsed;
        RemoteStatus.Text = remoteGateway is null ? "Шлюз не подключён." : "Шлюз " + remoteGateway.GatewayId.ToString("N")[..8];
        GoogleDriveStatus.Text = googleDriveSettings is null ? "Аккаунт пока не подключён." : "Подключено · папка «" + googleDriveSettings.FolderName + "».";
        FtpPanel.Visibility = kind == StorageKind.Ftp ? Visibility.Visible : Visibility.Collapsed;
        BrowseButton.Visibility = kind is StorageKind.Ftp or StorageKind.GoogleDrive or StorageKind.RemoteGateway ? Visibility.Collapsed : Visibility.Visible;
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

    private void GoogleDriveConnect_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new GoogleDriveWindow(googleDriveFolder, googleDriveSettings?.CredentialKey) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        googleDriveSettings = dialog.Settings; googleDriveFolder = dialog.FolderId; Destination.Text = googleDriveFolder;
        GoogleDriveStatus.Text = "Подключено · папка «" + googleDriveSettings!.FolderName + "».";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var kind = (StorageKind)Storage.SelectedValue;
        int? keepLast = null;
        if (!string.IsNullOrWhiteSpace(KeepLast.Text))
        {
            if (!int.TryParse(KeepLast.Text.Trim(), out var count) || count < 1)
            { Status.Text = "Укажите число хранимых копий: минимум 1, или оставьте поле пустым."; KeepLast.Focus(); return; }
            keepLast = count;
        }
        var port = 21;
        if (kind == StorageKind.Ftp && !int.TryParse(FtpPort.Text, out port))
        { Status.Text = "Порт FTP должен быть числом от 1 до 65535."; FtpPort.Focus(); return; }
        var destination = new BackupDestination { StorageKind = kind, DestinationDirectory = Destination.Text.Trim(),
            KeepLast = keepLast, GoogleDrive = kind == StorageKind.GoogleDrive ? googleDriveSettings : null,
            RemoteGateway = kind == StorageKind.RemoteGateway ? remoteGateway : null,
            Ftp = kind == StorageKind.Ftp ? new FtpSettings { Host = FtpHost.Text.Trim(), Port = port,
                UseTls = FtpTls.IsChecked == true, CredentialKey = credentialKey ?? "" } : null };
        var errors = destination.ApplyTo(new BackupJob { Name = "Хранилище", Source = "check", ArchiveCopies = true,
            ArchivePasswordKey = kind == StorageKind.RemoteGateway ? "validation-only" : null }).Validate().ToList();
        if (kind is StorageKind.LocalDirectory or StorageKind.NetworkShare && !Path.IsPathFullyQualified(destination.DestinationDirectory))
            errors.Add("Укажите полный путь к папке копий.");
        if (errors.Count != 0) { Status.Text = errors[0]; return; }
        Result = destination;
        DialogResult = true;
    }
    private async void RemoteImport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Подключение шлюза (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { remoteGateway = await new WindowsRemoteGatewayConnections(DesktopPaths.DataDirectory).ImportAsync(dialog.FileName); Destination.Text = "remote"; RemoteStatus.Text = "Шлюз " + remoteGateway.GatewayId.ToString("N")[..8]; }
        catch (Exception error) { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, error); Status.Text = "Не удалось импортировать подключение шлюза."; }
    }
}
