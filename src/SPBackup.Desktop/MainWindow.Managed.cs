using System.IO;
using System.Windows;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private bool BackgroundExecutionMatchesMode => agentSettings?.Mode == AgentMode.Managed
        ? backgroundState?.ExecutionMode == AgentMode.Managed
        : backgroundState?.ExecutionMode is null or AgentMode.Standalone;

    private bool ConfigurationWorkInProgress => changingAgentConfiguration || serviceManaging || installingUpdate
        || trayStopping || persisting || jobFileTransfer || activeRun is not null || manualPending
        || copyVerification is not null || schedulingTask is not null || editorOpen || backgroundStarting;

    private bool RequireConfigurationIdle()
    {
        if (!ConfigurationWorkInProgress && automationService is not (AutomationService.Starting or AutomationService.Running or AutomationService.Stopping)) return true;
        Status.Text = "Сначала остановите автоматическую работу и дождитесь окончания операций. Затем примените режим.";
        return false;
    }

    private async Task ValidateExecutionConnectionAsync()
    {
        if (agentSettings is null || !configurationReady) throw new InvalidOperationException("Сначала примените настройки агента.");
        if (agentSettings.Mode != AgentMode.Managed) return;
        var connection = await ServerMonitoringDispatcher.LoadSettingsAsync(DesktopPaths.DataDirectory)
            ?? throw new InvalidOperationException("Сначала подключитесь к серверу по приглашению.");
        _ = new ManagedAgentProfile(DesktopPaths.DataDirectory, agentSettings, connection);
    }

    private async Task ApplyAgentModeAsync()
    {
        if (agentSettings is null || !RequireConfigurationIdle()) return;
        using var lease = AcquireEngineOrReport(); if (lease is null) return;
        changingAgentConfiguration = true; UpdateRunControls();
        var saved = false;
        try
        {
            if (AgentModePicker.SelectedValue is not AgentMode mode) throw new InvalidOperationException("Выберите, откуда получать задания.");
            if (!int.TryParse(SyncInterval.Text, out var interval)) throw new InvalidOperationException("Укажите целое число секунд для интервала связи.");
            var connection = mode == AgentMode.Managed ? await ServerMonitoringDispatcher.LoadSettingsAsync(DesktopPaths.DataDirectory) : null;
            var updated = AgentModeTransition.Prepare(agentSettings, mode, connection, interval);
            await agentSettingsStore.SaveAsync(updated);
            saved = true;
            agentSettings = updated;
            AgentModePicker.SelectedValue = updated.Mode;
            await LoadEffectiveJobsAsync(); await RefreshHistoryAsync(); await RefreshServerConnectionLabelAsync();
        }
        catch (Exception error)
        {
            DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, error);
            AgentModePicker.SelectedValue = agentSettings.Mode;
            Status.Text = (saved ? "Режим сохранён, но задания не удалось загрузить: " : "Режим не применён: ") + error.Message;
        }
        finally { changingAgentConfiguration = false; UpdateRunControls(); }
    }

    private async Task RefreshServerConnectionLabelAsync()
    {
        try
        {
            var connection = await ServerMonitoringDispatcher.LoadSettingsAsync(DesktopPaths.DataDirectory);
            ServerAddress.Text = connection?.Address ?? "Подключение не настроено";
            if (connection is null) ServerMonitoringStatus.Text = "Подключитесь по приглашению из панели сервера. Затем выберите режим работы.";
        }
        catch (Exception) { ServerAddress.Text = "Не удалось прочитать подключение"; }
    }

    private async Task RefreshManagedJobsAsync()
    {
        if (agentSettings?.Mode != AgentMode.Managed) return;
        var snapshot = await new ManagedAgentProfile(DesktopPaths.DataDirectory, agentSettings).CreateConfiguration().GetSnapshotAsync(agentSettings);
        if (configurationReady && snapshot.Revision == managedRevision) return;
        var selectedId = (Jobs.SelectedItem as BackupJob)?.Id;
        await LoadEffectiveJobsAsync();
        Jobs.SelectedItem = jobs.FirstOrDefault(job => job.Id == selectedId);
    }
}
