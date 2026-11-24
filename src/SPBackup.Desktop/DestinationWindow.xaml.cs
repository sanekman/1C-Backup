using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class DestinationWindow : Window
{
    private readonly WindowsFtpCredentialStore credentials;
    private FtpSettings? ftpAccess;
    private bool ftpAccessEdited;
    private GoogleDriveSettings? googleDriveSettings;
    private string? googleDriveFolder;
    private RemoteGatewaySettings? remoteGateway;
    private NetworkShareSettings? networkShareSettings;
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
        ftpAccess = initial?.Ftp;
        googleDriveSettings = initial?.GoogleDrive;
        remoteGateway = initial?.RemoteGateway;
        networkShareSettings = initial?.NetworkShare;
        googleDriveFolder = initial?.StorageKind == StorageKind.GoogleDrive ? initial.DestinationDirectory : null;
        Destination.Text = initial?.DestinationDirectory ?? "";
        KeepLast.Text = initial?.KeepLast?.ToString() ?? "";
        FtpHost.Text = initial?.Ftp?.Host ?? "";
        FtpPort.Text = (initial?.Ftp?.Port ?? 21).ToString();
        FtpTls.IsChecked = initial?.Ftp?.UseTls ?? true;
        Storage.SelectedValue = initial?.StorageKind ?? StorageKind.LocalDirectory;
        NetworkUseCredentials.IsChecked = initial?.NetworkShare is not null; UpdateNetworkStatus();
        UpdateFtpAccessStatus();
        if (initial is not null) SaveButton.Content = "Применить";
    }

    private void Storage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (FtpPanel is null || BrowseButton is null || PathHint is null) return;
        var kind = Storage.SelectedValue is StorageKind selected ? selected : StorageKind.LocalDirectory;
        Height = kind == StorageKind.Ftp ? 620 : kind == StorageKind.NetworkShare ? 560 : 420;
        NetworkPanel.Visibility = kind == StorageKind.NetworkShare ? Visibility.Visible : Visibility.Collapsed;
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
            StorageKind.NetworkShare => @"Укажите сетевую папку: \\server\share\backups. Для NAS можно задать отдельный логин и пароль ниже.",
            StorageKind.Ftp => "Укажите папку на FTP-сервере, например /backups. FTPS проверяет сертификат сервера.",
            _ => @"Укажите полный путь, например D:\Backups. Папка будет создана при копировании."
        };
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Выберите папку для дополнительных копий" };
        if (dialog.ShowDialog(this) == true) Destination.Text = dialog.FolderName;
    }

    internal Action<FtpCredentialsWindow>? FtpCredentialsDialogOpened { get; set; }
    private string? FtpKeyFor(int port) =>
        FtpAccessDraft.KeyFor(ftpAccess, FtpHost.Text, port, FtpTls.IsChecked == true);

    private void FtpAddress_Changed(object sender, RoutedEventArgs e) => UpdateFtpAccessStatus();

    private void UpdateFtpAccessStatus()
    {
        if (CredentialsStatus is null) return;
        var key = int.TryParse(FtpPort.Text, out var port) ? FtpKeyFor(port) : null;
        FtpCredentialsButton.Content = key is null ? "Задать доступ FTP…" : "Изменить доступ FTP…";
        CredentialsStatus.Text = FtpAccessDraft.Status(ftpAccess, key, ftpAccessEdited);
    }

    private void Credentials_Click(object sender, RoutedEventArgs e)
    {
        var host = FtpHost.Text.Trim();
        var useTls = FtpTls.IsChecked == true;
        var error = FtpAccessDraft.ValidateAddress(host, FtpPort.Text, out var port);
        if (error is not null)
        {
            Status.Text = error;
            if (port is < 1 or > 65535) FtpPort.Focus();
            else FtpHost.Focus();
            return;
        }

        var key = "ftp-" + Guid.NewGuid().ToString("N");
        var dialog = new FtpCredentialsWindow(key, credentials) { Owner = this };
        dialog.DescribeStorageDraft(FtpAccessDraft.AddressLabel(host, port, useTls), credentials.TryReadUsername(FtpKeyFor(port)));
        FtpCredentialsDialogOpened?.Invoke(dialog);
        if (dialog.ShowDialog() != true) return;
        ftpAccess = new FtpSettings { Host = host, Port = port, UseTls = useTls, CredentialKey = key };
        ftpAccessEdited = true;
        Status.Text = "";
        UpdateFtpAccessStatus();
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
        if (kind == StorageKind.Ftp)
        {
            var error = FtpAccessDraft.ValidateAddress(FtpHost.Text, FtpPort.Text, out port);
            if (error is not null) { Status.Text = error; return; }
            if (FtpKeyFor(port) is null)
            {
                Status.Text = "Нажмите «Задать доступ FTP…» и введите данные для указанного сервера.";
                FtpCredentialsButton.BringIntoView();
                FtpCredentialsButton.Focus();
                return;
            }
        }
        var destination = new BackupDestination { StorageKind = kind, DestinationDirectory = Destination.Text.Trim(),
            NetworkShare = kind == StorageKind.NetworkShare && NetworkUseCredentials.IsChecked == true ? networkShareSettings ?? new() : null,
            KeepLast = keepLast, GoogleDrive = kind == StorageKind.GoogleDrive ? googleDriveSettings : null,
            RemoteGateway = kind == StorageKind.RemoteGateway ? remoteGateway : null,
            Ftp = kind == StorageKind.Ftp ? new FtpSettings { Host = FtpHost.Text.Trim(), Port = port,
                UseTls = FtpTls.IsChecked == true, CredentialKey = FtpKeyFor(port) ?? "" } : null };
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
