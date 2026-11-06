using System.Net;
using System.Text.Json;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class ManagedExecutionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sp-managed-" + Guid.NewGuid().ToString("N"));
    private readonly AgentSettings settings = new() { Mode = AgentMode.Managed, ServerAddress = "https://localhost:9/", CredentialKey = "enrolled-key" };
    private ManagedAgentProfile Profile => new(root, settings);
    private BackupJob Job => new() { Id = jobId, Name = "Server job", Kind = DatabaseKind.FilesAndFolders,
        Source = Path.Combine(root, "source"), DestinationDirectory = Path.Combine(root, "copies"),
        ScheduleKind = ScheduleKind.SystemStartup, FileBackupMode = FileBackupMode.Incremental, KeepLast = 1 };
    private readonly Guid jobId = Guid.NewGuid();
    private sealed class NoCredentials : IFtpCredentialStore
    { public Task<NetworkCredential> GetAsync(string key, CancellationToken token = default) => throw new InvalidOperationException("Test enrollment is offline"); }
    private sealed class UnusedExecutor : IBackupExecutor
    { public Task<string> CreateAsync(BackupJob job, string work, CancellationToken token = default) => throw new InvalidOperationException("Files use the real built-in executor"); }
    public ManagedExecutionTests() { Directory.CreateDirectory(Job.Source); File.WriteAllText(Path.Combine(Job.Source, "data.txt"), "backup data"); }
    private async Task Setup(BackupJob job)
    {
        await new AgentSettingsStore(Path.Combine(root, "agent.json")).SaveAsync(settings);
        await ServerMonitoringDispatcher.SaveSettingsAsync(root, new(settings.ServerAddress, settings.AgentId, settings.CredentialKey));
        await new AgentConfigurationCache(Profile.CachePath).SaveAsync(new(settings.NormalizedServerAddress,
            new(settings.AgentId, 7, [job]), DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task EnrollmentScopesSeparateCacheCatalogStartupAndLegacyFiles()
    {
        var first = Profile;
        var equivalent = new ManagedAgentProfile(root, settings with { ServerAddress = "https://LOCALHOST:9" });
        Assert.Equal(first.DirectoryPath, equivalent.DirectoryPath);
        var other = new ManagedAgentProfile(root, settings with { CredentialKey = "new-enrollment" });
        Assert.NotEqual(first.DirectoryPath, other.DirectoryPath);
        Assert.DoesNotContain(settings.CredentialKey, first.DirectoryPath);
        await File.WriteAllTextAsync(Path.Combine(root, "server-jobs.json"), "legacy marker");
        await new JobStore(Path.Combine(root, "jobs.json")).SaveAsync([Job with { Name = "Local job" }]);
        await Setup(Job);
        Assert.Single(await equivalent.CreateConfiguration().GetEffectiveJobsAsync(equivalent.Settings));
        Assert.Empty(await other.CreateConfiguration().GetEffectiveJobsAsync(other.Settings));
        var boot = Guid.NewGuid();
        Assert.True(await new StartupBackupSchedule(first.StartupSchedulePath, () => boot).TryConsumeAsync(jobId));
        Assert.False(await new StartupBackupSchedule(equivalent.StartupSchedulePath, () => boot).TryConsumeAsync(jobId));
        Assert.True(await new StartupBackupSchedule(other.StartupSchedulePath, () => boot).TryConsumeAsync(jobId));
        Assert.True(await new StartupBackupSchedule(Path.Combine(root, "startup-schedule.json"), () => boot).TryConsumeAsync(jobId));
        Assert.Equal("legacy marker", await File.ReadAllTextAsync(Path.Combine(root, "server-jobs.json")));
    }

    [Fact]
    public async Task OfflineManagedHostCreatesIndependentFullCopyAndRetainsSelectedRevision()
    {
        var local = Job with { Name = "Local job" };
        var localStore = new JobStore(Path.Combine(root, "jobs.json")); await localStore.SaveAsync([local]);
        var original = await File.ReadAllBytesAsync(Path.Combine(root, "jobs.json"));
        var history = new BackupRunHistory(Path.Combine(root, "runs.json"));
        var standaloneCatalog = new BackupCatalog(Path.Combine(root, "copies.json"));
        var standalone = await new LocalBackupRunner(new UnusedExecutor(), new(new FileBackupStorage(), standaloneCatalog), history, Path.Combine(root, "work")).RunAsync(local);
        await Setup(Job);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Assert.Equal(0, await new LocalAgentHost(root, new UnusedExecutor(), new NoCredentials()).RunAsync(
            pollInterval: TimeSpan.FromMilliseconds(20), report: _ => { stop.Cancel(); return Task.CompletedTask; }, cancellationToken: stop.Token));
        var run = (await history.ReadAsync()).Single(r => r.Configuration is not null);
        Assert.Equal(BackupRunStatus.Succeeded, run.Status);
        Assert.Equal(new BackupConfigurationStamp(Profile.ConnectionId, 7), run.Configuration);
        Assert.True(run.Backup!.FileSnapshot!.IsFull); // Never extends a same-ID standalone chain.
        Assert.True(File.Exists(standalone.Backup!.Location)); Assert.True(File.Exists(run.Backup.Location));
        Assert.Single(await standaloneCatalog.ReadAsync()); Assert.Single(await new BackupCatalog(Profile.CatalogPath).ReadAsync());
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(root, "jobs.json")));
        Assert.Equal(7, ServerMonitoringDispatcher.ToReport(run).ConfigurationRevision);
        Assert.DoesNotContain(Profile.ConnectionId, JsonSerializer.Serialize(ServerMonitoringDispatcher.ToReport(run)));
        var selected = Assert.Single(await new AgentExecutionJobSource(Profile.CreateConfiguration(), settings).LoadAsync());
        Assert.DoesNotContain(Profile.ConnectionId, JsonSerializer.Serialize(selected));
        var state = await new LocalAgentControlStore(root).ReadAsync(); Assert.Equal(AgentMode.Managed, state!.ExecutionMode);
    }

    [Fact]
    public async Task OfflineManagedManualQueueRunsDisabledServerJobInsteadOfSameIdLocalJob()
    {
        await new JobStore(Path.Combine(root, "jobs.json")).SaveAsync([Job with { Name = "Local job" }]);
        await Setup(Job with { Enabled = false, ScheduleKind = ScheduleKind.Manual });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var host = new LocalAgentHost(root, new UnusedExecutor(), new NoCredentials()).RunAsync(pollInterval: TimeSpan.FromMilliseconds(20), cancellationToken: stop.Token);
        try
        {
            var control = new LocalAgentControlStore(root); LocalAgentState? state;
            while ((state = await control.ReadAsync(stop.Token)) is not { CanRunManually: true, ConfigurationRevision: 7 }) await Task.Delay(20, stop.Token);
            var queue = new LocalManualRunQueue(root); var request = await queue.EnqueueAsync(state.SessionId, jobId, stop.Token);
            LocalManualRunReceipt? receipt;
            while ((receipt = await queue.ReadReceiptAsync(request.RequestId, stop.Token)) is null or { Phase: LocalManualRunPhase.Accepted }) await Task.Delay(20, stop.Token);
            Assert.Equal(LocalManualRunPhase.Finished, receipt.Phase);
            var run = Assert.Single(await new BackupRunHistory(Path.Combine(root, "runs.json")).ReadAsync());
            Assert.Equal("Server job", run.JobName); Assert.Equal(7, run.Configuration!.Revision);
        }
        finally { stop.Cancel(); Assert.Equal(0, await host); }
    }

    [Fact]
    public async Task InterruptedHistoryKeepsItsOriginalRevisionAndRejectsCorruptStamp()
    {
        var history = new BackupRunHistory(Path.Combine(root, "runs.json"));
        var run = new BackupRun(Guid.NewGuid(), jobId, "Server", DateTimeOffset.UtcNow, null, BackupRunStatus.Running, root)
            { Configuration = new(Profile.ConnectionId, 3) };
        await history.SaveAsync(run); await history.RecoverInterruptedAsync();
        var restored = Assert.Single(await history.ReadAsync()); Assert.Equal(BackupRunStatus.Interrupted, restored.Status);
        Assert.Equal(run.Configuration, restored.Configuration);
        await Assert.ThrowsAsync<InvalidDataException>(() => history.SaveAsync(restored with { Configuration = new("corrupt", 3) }));
        Assert.Equal(run.Configuration, Assert.Single(await history.ReadAsync()).Configuration);
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("address")]
    [InlineData("key")]
    public void MismatchedEnrollmentCannotConstructManagedProfile(string fault)
    {
        var connection = new ServerMonitoringSettings(settings.ServerAddress, settings.AgentId, settings.CredentialKey);
        connection = fault switch { "agent" => connection with { AgentId = Guid.NewGuid() },
            "address" => connection with { Address = "https://other.example.test/" }, _ => connection with { CredentialKey = "other" } };
        Assert.Throws<InvalidDataException>(() => new ManagedAgentProfile(root, settings, connection));
    }
    public void Dispose() => Directory.Delete(root, true);
}
