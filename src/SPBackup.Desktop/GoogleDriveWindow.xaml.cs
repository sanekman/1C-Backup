using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class GoogleDriveWindow : Window
{
    private GoogleDriveAuthorization.DesktopClient? client;
    private readonly string? existingFolder;
    private readonly string? credentialKey;
    private readonly CancellationTokenSource cancellation = new();
    public GoogleDriveSettings? Settings { get; private set; }
    public string? FolderId { get; private set; }
    public GoogleDriveWindow(string? existingFolder = null, string? credentialKey = null)
    {
        this.existingFolder = existingFolder; this.credentialKey = credentialKey;
        InitializeComponent();
        if (existingFolder is not null) ConnectButton.Content = "2. Войти и подтвердить доступ к прежней папке";
        Closed += (_, _) => cancellation.Cancel();
        if (credentialKey is not null) Loaded += async (_, _) =>
        {
            try
            {
                var saved = GoogleDriveCredentials.Parse(await new WindowsGoogleDriveCredentialStore().GetAsync(credentialKey, cancellation.Token));
                if (client is null && !cancellation.IsCancellationRequested)
                {
                    client = new(saved.ClientId, saved.ClientSecret);
                    ClientStatus.Text = "Используется прежний OAuth-клиент. Повторно выбирать JSON не нужно.";
                    ConnectButton.IsEnabled = true;
                }
            }
            catch (Exception) { if (!cancellation.IsCancellationRequested) ClientStatus.Text = "Прежние настройки недоступны. Выберите исходный OAuth JSON."; }
        };
    }
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "OAuth-клиент Google (Desktop app)", Filter = "OAuth JSON (*.json)|*.json" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(picker.FileName).Length > 65536) throw new InvalidDataException("Файл настроек слишком большой.");
            client = GoogleDriveAuthorization.ParseDesktopClient(File.ReadAllText(picker.FileName));
            ClientStatus.Text = "OAuth-клиент выбран. Можно открыть вход Google."; ConnectButton.IsEnabled = true; Status.Text = "";
        }
        catch (Exception ex)
        { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Warning, ex); client = null; ConnectButton.IsEnabled = false; Status.Text = "Не удалось прочитать OAuth JSON. Выберите файл клиента Desktop app."; }
    }
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (client is null) return;
        ConnectButton.IsEnabled = ImportButton.IsEnabled = false;
        Status.Text = "Завершите вход и подтвердите разрешение в браузере. Ожидание — до 5 минут.";
        try
        {
            using var http = GoogleDriveClient.CreateHttpClient();
            var credentials = await GoogleDriveAuthorization.ConnectAsync(client, http,
                url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }), cancellation.Token);
            Status.Text = "Создание папки для копий…";
            var drive = new GoogleDriveClient(http, credentials);
            var folder = existingFolder ?? await drive.CreateFolderAsync(cancellation.Token);
            await drive.CheckFolderAsync(folder, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var key = credentialKey ?? "google-" + Guid.NewGuid().ToString("N");
            new WindowsGoogleDriveCredentialStore().Save(key, credentials);
            Settings = new GoogleDriveSettings { CredentialKey = key }; FolderId = folder;
            DialogResult = true;
        }
        catch (OperationCanceledException) { if (!cancellation.IsCancellationRequested) Status.Text = "Время ожидания истекло. Повторите вход."; }
        catch (Exception ex)
        { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Warning, ex); Status.Text = "Подключение не завершено. Проверьте OAuth-клиент, разрешение аккаунту и доступ к Google."; }
        finally { if (!cancellation.IsCancellationRequested) { ImportButton.IsEnabled = true; ConnectButton.IsEnabled = client is not null; } }
    }
}
