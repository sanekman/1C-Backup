using System.IO;
using System.Net;
using System.Windows;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private sealed class OfflineManagedCredentials : IFtpCredentialStore
    {
        public Task<NetworkCredential> GetAsync(string key, CancellationToken token = default) =>
            throw new InvalidOperationException("Isolated offline enrollment");
    }

    internal async Task SmokeVerifyManagedModeAsync(Action<string> screenshot)
    {
        if (!SmokeMode || agentSettings is null) throw new InvalidOperationException("An isolated smoke profile is required.");
        var original = agentSettings;
        var root = DesktopPaths.DataDirectory;
        var files = new[] { "jobs.json", "server-monitoring.json", "runs.json" };
        var saved = new Dictionary<string, byte[]?>();
        foreach (var name in files) saved[name] = File.Exists(Path.Combine(root, name)) ? await File.ReadAllBytesAsync(Path.Combine(root, name)) : null;
        var source = Path.Combine(Path.GetDirectoryName(root)!, "managed-ui-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source); await File.WriteAllTextAsync(Path.Combine(source, "data.txt"), "Managed UI backup");
        var connection = new ServerMonitoringSettings("https://localhost:9/prefix/", original.AgentId, "ui-smoke-" + Guid.NewGuid().ToString("N"));
        var managed = AgentModeTransition.Prepare(original, AgentMode.Managed, connection);
        var profile = new ManagedAgentProfile(root, managed, connection);
        var localJob = new BackupJob { Name = "Local same-ID job", Kind = DatabaseKind.FilesAndFolders, Source = source,
            DestinationDirectory = source + "-copies", Enabled = false, ScheduleKind = ScheduleKind.Manual };
        var serverJob = localJob with { Name = "Server manual job" };
        using var stop = new CancellationTokenSource();
        Task<int>? host = null;
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        async Task WaitAsync(Func<Task<bool>> test, string message)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (!await test()) { if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException(message); await Task.Delay(50); }
        }
        try
        {
            File.Delete(Path.Combine(root, "server-monitoring.json"));
            AgentModePicker.SelectedValue = AgentMode.Managed;
            await ApplyAgentModeAsync();
            Require(agentSettings.Mode == AgentMode.Standalone && Status.Text.Contains("Режим не применён"), "Missing enrollment enabled Managed mode.");
            await store.SaveAsync([localJob]);
            var localBytes = await File.ReadAllBytesAsync(Path.Combine(root, "jobs.json"));
            await ServerMonitoringDispatcher.SaveSettingsAsync(root, connection);
            AgentModePicker.SelectedValue = AgentMode.Managed;
            await ApplyAgentModeAsync();
            Require(agentSettings == managed && configurationReady && !canSave && jobs.Count == 0 && StartBackgroundButton.IsEnabled,
                "An enrolled agent cannot start before receiving its first configuration.");
            Require(ServerAddress.IsReadOnly && ServerAddress.Text == connection.Address && FindName("AgentCredentialKey") is null,
                "Enrollment requires entering technical credential references.");
            await new AgentConfigurationCache(profile.CachePath).SaveAsync(new(managed.NormalizedServerAddress,
                new(managed.AgentId, 11, [serverJob]), DateTimeOffset.UtcNow));
            await RefreshManagedJobsAsync(); Jobs.SelectedItem = jobs.Single(); UpdateRunControls();
            Require(jobs.Single().Name == serverJob.Name && !NewJobButton.IsEnabled && !EditJobButton.IsEnabled && !RunButton.IsEnabled,
                "Server jobs can be edited or run through the local window executor.");
            var localRun = new BackupRun(Guid.NewGuid(), localJob.Id, localJob.Name, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                BackupRunStatus.Succeeded, source);
            await history.SaveAsync(localRun); await RefreshHistoryAsync();
            Require(historyRuns.All(run => run.RunId != localRun.RunId), "Local history leaked into the server profile.");
            host = Task.Run(() => new LocalAgentHost(root, new DatabaseBackupExecutor(new BackupProcessRunner()), new OfflineManagedCredentials())
                .RunAsync(pollInterval: TimeSpan.FromMilliseconds(30), cancellationToken: stop.Token));
            await WaitAsync(async () => { await RefreshBackgroundStateAsync(); return BackgroundAcceptsManualRuns; }, "Managed UI did not connect to its host.");
            Require(RunButton.IsEnabled, "Manual disabled server job is blocked while the host is running.");
            AgentModePicker.SelectedValue = AgentMode.Standalone;
            await ApplyAgentModeAsync();
            Require(agentSettings.Mode == AgentMode.Managed, "Mode changed while the host owned the execution lease.");
            AgentModePicker.SelectedValue = AgentMode.Managed;
            await RunManualAsync(jobs.Single());
            await WaitAsync(async () => (await history.ReadAsync()).Any(run => run.Configuration?.ConnectionId == profile.ConnectionId
                && run.Status == BackupRunStatus.Succeeded), "Managed manual UI backup did not finish.");
            await RefreshHistoryAsync(); await RefreshCopiesAsync();
            var run = historyRuns.Single();
            Require(run.JobName == serverJob.Name && run.Configuration?.Revision == 11 && File.Exists(run.Backup!.Location),
                "Manual UI backup used another profile or lost its configuration revision.");
            ShowPage("jobs"); UpdateLayout(); screenshot("managed-manual-offline");
            stop.Cancel(); Require(await host.WaitAsync(TimeSpan.FromSeconds(10)) == 0, "Managed host did not stop."); host = null;
            await RefreshBackgroundStateAsync();
            await File.WriteAllTextAsync(Path.Combine(root, "server-monitoring.json"), "broken enrollment");
            AgentModePicker.SelectedValue = AgentMode.Standalone;
            await ApplyAgentModeAsync();
            Require(agentSettings.Mode == AgentMode.Standalone && canSave && jobs.Single().Name == localJob.Name,
                "Returning to local jobs requires repairing an unrelated connection.");
            Require(historyRuns.Any(item => item.RunId == localRun.RunId) && historyRuns.All(item => item.Configuration is null),
                "Local history was lost or mixed with managed runs.");
            var localAfter = await File.ReadAllBytesAsync(Path.Combine(root, "jobs.json"));
            Require(localBytes.SequenceEqual(localAfter), "Changing modes rewrote local jobs.");
        }
        finally
        {
            stop.Cancel(); if (host is not null) await host.WaitAsync(TimeSpan.FromSeconds(10));
            foreach (var pair in saved)
                if (pair.Value is null) File.Delete(Path.Combine(root, pair.Key)); else await File.WriteAllBytesAsync(Path.Combine(root, pair.Key), pair.Value);
            await agentSettingsStore.SaveAsync(original); agentSettings = original; AgentModePicker.SelectedValue = original.Mode;
            backgroundStarting = false;
            await LoadEffectiveJobsAsync(); await RefreshBackgroundStateAsync(); await RefreshHistoryAsync(); await RefreshServerConnectionLabelAsync();
            if (Directory.Exists(profile.DirectoryPath)) Directory.Delete(profile.DirectoryPath, true);
        }
    }
}
