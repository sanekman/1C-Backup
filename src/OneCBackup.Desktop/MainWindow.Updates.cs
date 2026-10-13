using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

public partial class MainWindow
{
    private ApplicationUpdateSettings updateSettings = new();
    private readonly ApplicationUpdateSettingsStore updateSettingsStore = new(Path.Combine(DesktopPaths.DataDirectory, "updates.json"));
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private ApplicationUpdate? availableUpdate;
    private string? preparedUpdatePath;
    private bool updateBusy, savingUpdatePreferences, updateInitialized, installingUpdate;
    private CancellationTokenSource? updateCancellation;

    private async Task InitializeUpdatesAsync()
    {
        try
        {
            updateSettings = await updateSettingsStore.LoadAsync();
            AutomaticUpdateChecks.IsChecked = updateSettings.AutomaticChecks;
            UpdateOnExit.IsChecked = updateSettings.UpdateOnExit;
            IncludePreviewUpdates.IsChecked = updateSettings.IncludePreview;
            UpdateTokenStatus.Text = updateSettings.HasGitHubToken ? "Токен сохранён в Windows." : "Токен не сохранён. Для закрытого репозитория он необходим.";
            var resultPath = Path.Combine(DesktopPaths.DataDirectory, "updates", "update-result.json");
            if (File.Exists(resultPath) && new FileInfo(resultPath).Length <= 8192)
            {
                using var result = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath));
                DiagnosticLog.Current.Write(DiagnosticEvent.UpdateResultLoaded, values: new(Code: result.RootElement.GetProperty("Success").GetBoolean() ? 0 : 1));
                UpdateResultStatus.Visibility = Visibility.Visible;
                UpdateResultStatus.Text = result.RootElement.GetProperty("Success").GetBoolean()
                    ? "Текущая версия: " + ApplicationBuildInfo.Version + ". Последняя установка через обновление: " + result.RootElement.GetProperty("Version").GetString() + "."
                    : "Последнее обновление не установлено: " + result.RootElement.GetProperty("Message").GetString();
            }
            updateInitialized = true; UpdateUpdateControls();
            if (SmokeMode) return;
            updateTimer.Tick += async (_, _) => { if (updateSettings.AutomaticChecks && !trayStopping) await CheckUpdatesAsync(); };
            updateTimer.Start();
            if (updateSettings.AutomaticChecks) await CheckUpdatesAsync();
        }
        catch (Exception error) { UpdateStatus.Text = "Не удалось загрузить настройки обновлений: " + error.Message; }
    }

    private void UpdateUpdateControls()
    {
        CheckUpdatesButton.IsEnabled = updateInitialized && !updateBusy && !trayStopping;
        UpdateReleaseButton.IsEnabled = availableUpdate is not null && !trayStopping;
        InstallUpdateButton.IsEnabled = updateInitialized && availableUpdate is not null && !updateBusy && !trayStopping;
        AutomaticUpdateChecks.IsEnabled = UpdateOnExit.IsEnabled = IncludePreviewUpdates.IsEnabled = updateInitialized && !updateBusy && !savingUpdatePreferences;
        UpdateAccessPanel.IsEnabled = updateInitialized && !updateBusy && !savingUpdatePreferences;
        CancelUpdateButton.Visibility = updateBusy ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task<string?> UpdateSecretAsync(CancellationToken token) => updateSettings.HasGitHubToken
        ? await new WindowsUpdateCredentialStore().GetAsync(token) : null;

    private async void UpdatePreference_Click(object sender, RoutedEventArgs e)
    {
        if (!updateInitialized || savingUpdatePreferences || updateBusy) return;
        savingUpdatePreferences = true; UpdateUpdateControls();
        var previous = updateSettings;
        try
        {
            updateSettings = updateSettings with { AutomaticChecks = AutomaticUpdateChecks.IsChecked == true,
                UpdateOnExit = UpdateOnExit.IsChecked == true, IncludePreview = IncludePreviewUpdates.IsChecked == true };
            if (updateSettings.UpdateOnExit) { updateSettings = updateSettings with { AutomaticChecks = true }; AutomaticUpdateChecks.IsChecked = true; }
            await updateSettingsStore.SaveAsync(updateSettings);
            if (previous.IncludePreview != updateSettings.IncludePreview) { availableUpdate = null; preparedUpdatePath = null; }
            UpdateStatus.Text = updateSettings.UpdateOnExit ? "Автообновление включено: установка при полном выходе после остановки агента."
                : "Настройки обновлений сохранены.";
        }
        catch (Exception error)
        {
            updateSettings = previous;
            AutomaticUpdateChecks.IsChecked = previous.AutomaticChecks; UpdateOnExit.IsChecked = previous.UpdateOnExit;
            IncludePreviewUpdates.IsChecked = previous.IncludePreview;
            UpdateStatus.Text = "Не удалось сохранить настройки обновлений: " + error.Message;
        }
        finally { savingUpdatePreferences = false; UpdateUpdateControls(); }
        if (!SmokeMode && updateSettings.AutomaticChecks) await CheckUpdatesAsync();
    }
    private async void SaveUpdateToken_Click(object sender, RoutedEventArgs e)
    {
        if (!updateInitialized || updateBusy || savingUpdatePreferences) return;
        savingUpdatePreferences = true; UpdateUpdateControls();
        try
        {
            new WindowsUpdateCredentialStore().Save(GitHubUpdateToken.Password.Trim());
            GitHubUpdateToken.Clear();
            updateSettings = updateSettings with { HasGitHubToken = true };
            await updateSettingsStore.SaveAsync(updateSettings);
            UpdateTokenStatus.Text = "Токен сохранён в защищённом хранилище Windows.";
        }
        catch (Exception error) { UpdateTokenStatus.Text = "Не удалось сохранить доступ: " + error.Message; }
        finally { GitHubUpdateToken.Clear(); savingUpdatePreferences = false; UpdateUpdateControls(); }
        if (updateSettings.HasGitHubToken) await CheckUpdatesAsync();
    }
    private async void DeleteUpdateToken_Click(object sender, RoutedEventArgs e)
    {
        if (!updateInitialized || updateBusy || savingUpdatePreferences) return;
        savingUpdatePreferences = true; UpdateUpdateControls();
        try
        {
            new WindowsUpdateCredentialStore().Delete();
            updateSettings = updateSettings with { HasGitHubToken = false };
            await updateSettingsStore.SaveAsync(updateSettings);
            GitHubUpdateToken.Clear(); UpdateTokenStatus.Text = "Токен удалён из Windows.";
        }
        catch (Exception error) { UpdateTokenStatus.Text = error.Message; }
        finally { savingUpdatePreferences = false; UpdateUpdateControls(); }
    }
    private void OpenUpdateRelease_Click(object sender, RoutedEventArgs e)
    {
        if (availableUpdate is null) return;
        OpenUpdateWebPage(availableUpdate.ReleaseAddress);
    }
    private void CreateUpdateToken_Click(object sender, RoutedEventArgs e) => OpenUpdateWebPage("https://github.com/settings/personal-access-tokens/new");
    private void OpenUpdateWebPage(string address)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(address) { UseShellExecute = true }); }
        catch (Exception error) { UpdateStatus.Text = "Не удалось открыть браузер: " + error.Message; }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckUpdatesAsync();
    private void CancelUpdate_Click(object sender, RoutedEventArgs e) => updateCancellation?.Cancel();

    private async Task CheckUpdatesAsync()
    {
        if (!updateInitialized || updateBusy || trayStopping || savingUpdatePreferences) return;
        updateBusy = true; updateCancellation = new(); UpdateUpdateControls();
        var token = updateCancellation.Token;
        using var client = GitHubApplicationUpdates.CreateClient();
        try
        {
            DiagnosticLog.Current.Write(DiagnosticEvent.UpdateCheckStarted);
            UpdateStatus.Text = "Проверка новых версий…";
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var secret = await UpdateSecretAsync(token);
            var candidate = await new GitHubApplicationUpdates(client).CheckAsync(ApplicationBuildInfo.Version, updateSettings.IncludePreview, secret, deadline.Token, UpdateRetryProgress());
            if (candidate?.Manifest.Version != availableUpdate?.Manifest.Version) preparedUpdatePath = null;
            availableUpdate = candidate;
            DiagnosticLog.Current.Write(DiagnosticEvent.UpdateCheckCompleted, values: new(Count: candidate is null ? 0 : 1));
            if (candidate is null) { UpdateStatus.Text = "Установлена актуальная версия."; return; }
            UpdateStatus.Text = "Доступна версия " + candidate.Manifest.Version + "."
                + (ApplicationUpdateHandoff.IsInstalled() ? "" : " Переносную версию обновите вручную из ZIP релиза.");
            if (updateSettings.UpdateOnExit && ApplicationUpdateHandoff.IsInstalled())
            {
                preparedUpdatePath = await DownloadUpdateAsync(client, candidate, secret, token);
                UpdateStatus.Text = "Версия " + candidate.Manifest.Version + " скачана и проверена. Установится при «Выйти полностью».";
            }
        }
        catch (OperationCanceledException) { UpdateStatus.Text = "Проверка или скачивание отменены либо истекло время ожидания."; }
        catch (Exception error)
        {
            DiagnosticLog.Current.Write(DiagnosticEvent.UpdateFailed, DiagnosticLevel.Warning, error);
            UpdateStatus.Text = "Обновление недоступно: " + error.Message;
        }
        finally
        {
            updateCancellation.Dispose(); updateCancellation = null; updateBusy = false;
            UpdateProgress.Visibility = Visibility.Collapsed; UpdateUpdateControls();
        }
    }
    private async Task<string> DownloadUpdateAsync(HttpClient client, ApplicationUpdate candidate, string? secret, CancellationToken token)
    {
        UpdateProgress.Visibility = Visibility.Visible; UpdateProgress.Value = 0;
        var active = true;
        var progress = new Progress<int>(percent =>
        {
            if (!active || !updateBusy) return;
            UpdateProgress.Value = percent;
            UpdateStatus.Text = "Скачивание " + candidate.Manifest.Version + ": " + percent + "%";
        });
        string path;
        try { path = await new GitHubApplicationUpdates(client).DownloadAsync(candidate, Path.Combine(DesktopPaths.DataDirectory, "updates"), secret, progress, token, UpdateRetryProgress(candidate.Manifest.Version)); }
        finally { active = false; }
        DiagnosticLog.Current.Write(DiagnosticEvent.UpdateDownloaded, values: new(Bytes: candidate.Manifest.Size));
        // Keep the current and one previous download slot; never touch arbitrary EXEs or active helper files.
        foreach (var old in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.exe")
            .Where(file => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file), "^[a-f0-9]{64}\\.exe$"))
            .OrderByDescending(File.GetLastWriteTimeUtc).Where(file => file != path).Skip(1))
        {
            try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return path;
    }
    private IProgress<UpdateRetryNotice> UpdateRetryProgress(string? version = null) => new Progress<UpdateRetryNotice>(notice =>
    {
        if (!updateBusy) return;
        DiagnosticLog.Current.Write(DiagnosticEvent.UpdateRetrying, DiagnosticLevel.Warning, values: new(Count: notice.Attempt, Code: (int)notice.Phase));
        UpdateStatus.Text = "Соединение прервалось. Повторная " + (notice.Phase == UpdateRequestPhase.Checking ? "проверка" : "загрузка " + version)
            + ": попытка " + notice.Attempt + " из " + notice.TotalAttempts + ".";
    });
    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (updateBusy || trayStopping || availableUpdate is null) return;
        if (editorOpen || OwnedWindows.Cast<Window>().Any(window => window.IsVisible))
        { UpdateStatus.Text = "Сохраните или отмените изменения и закройте дополнительные окна перед обновлением."; return; }
        if (!ApplicationUpdateHandoff.IsInstalled())
        { UpdateStatus.Text = "Автообновление доступно для установленной программы. Переносную версию обновите вручную из ZIP релиза."; return; }
        if (MessageBox.Show(this, "Программа остановит агент и перезапустится после установки. Выполняемая копия будет отменена. Задания и готовые копии сохранятся. Обновить?",
            "Обновление 1C-Backup", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
        var candidate = availableUpdate;
        installingUpdate = true; UpdateRunControls();
        updateBusy = true; updateCancellation = new(); UpdateUpdateControls();
        try
        {
            using var client = GitHubApplicationUpdates.CreateClient();
            preparedUpdatePath = await DownloadUpdateAsync(client, candidate, await UpdateSecretAsync(updateCancellation.Token), updateCancellation.Token);
        }
        catch (OperationCanceledException) { UpdateStatus.Text = "Скачивание отменено."; return; }
        catch (Exception error)
        { DiagnosticLog.Current.Write(DiagnosticEvent.UpdateFailed, DiagnosticLevel.Warning, error); UpdateStatus.Text = "Обновление не установлено: " + error.Message; return; }
        finally
        {
            updateCancellation.Dispose(); updateCancellation = null; updateBusy = false;
            installingUpdate = false; UpdateRunControls();
            UpdateProgress.Visibility = Visibility.Collapsed; UpdateUpdateControls();
        }
        var restartAgent = backgroundBusy || backgroundStarting;
        var restartWindowSchedule = schedulingTask is not null;
        await StopFromTrayAsync(true, () => ApplicationUpdateHandoff.StartAsync(candidate, preparedUpdatePath!, restart: true, restartAgent, restartWindowSchedule));
    }
}
