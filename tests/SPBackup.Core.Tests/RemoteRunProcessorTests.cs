using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class RemoteRunProcessorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "remote-commands-" + Guid.NewGuid().ToString("N"));
    private readonly AgentSettings settings = new() { AgentId = Guid.NewGuid(), Mode = AgentMode.Managed, ServerAddress = "https://localhost/", CredentialKey = "enrolled" };
    private readonly BackupJob job;
    private BackupRun? lastRun;
    private int executed;
    private readonly Clock clock = new();
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Api : IRemoteRunApi
    {
        public IReadOnlyList<RemoteRunCommand> Commands = [];
        public List<RemoteRunReceipt> Receipts = [];
        public bool LoseFinalReply;
        public bool LoseStartReply;
        public Task<IReadOnlyList<RemoteRunCommand>> PollCommandsAsync(AgentSettings settings, CancellationToken token) => Task.FromResult(Commands);
        public Task SendCommandReceiptAsync(AgentSettings settings, RemoteRunReceipt receipt, CancellationToken token)
        {
            Receipts.Add(receipt);
            if (receipt.Status == "Running" && LoseStartReply) throw new IOException("Lost start reply");
            if (receipt.Terminal && LoseFinalReply) { LoseFinalReply = false; throw new IOException("Lost committed reply"); }
            return Task.CompletedTask;
        }
    }
    public RemoteRunProcessorTests()
    {
        Directory.CreateDirectory(root);
        job = new() { Kind = DatabaseKind.OneCFile, Name = "Saved manual job", Source = root, DestinationDirectory = root + "-copies",
            OneCMethod = OneCBackupMethod.FileArchive, ScheduleKind = ScheduleKind.Manual, Enabled = false };
    }
    private async Task<(RemoteRunProcessor Processor, Api Api, LocalBackupDispatcher Dispatcher, string Journal)> Create()
    {
        var profile = new ManagedAgentProfile(root, settings);
        await new AgentConfigurationCache(profile.CachePath).SaveAsync(new(settings.NormalizedServerAddress, new(settings.AgentId, 3, [job]), clock.Now));
        var source = new AgentExecutionJobSource(profile.CreateConfiguration(), settings);
        var dispatcher = new LocalBackupDispatcher(source, profile.StartupSchedulePath, (selected, token) =>
        {
            Assert.Equal(job.Id, selected.Id);
            Interlocked.Increment(ref executed);
            lastRun = new(Guid.NewGuid(), selected.Id, selected.Name, clock.Now, clock.Now, BackupRunStatus.Succeeded, root);
            return Task.CompletedTask;
        });
        var api = new Api { Commands = [new(Guid.NewGuid(), job.Id, 3, clock.Now.AddMinutes(10))] };
        var journal = Path.Combine(profile.DirectoryPath, "remote-runs");
        return (new(journal, settings, api, source, dispatcher, () => lastRun, clock), api, dispatcher, journal);
    }
    [Fact]
    public async Task LostFinalReplyAndRepeatedDeliveryExecuteDisabledManualJobOnlyOnce()
    {
        var (processor, api, _, _) = await Create(); api.LoseFinalReply = true;
        await Assert.ThrowsAsync<IOException>(() => processor.PollOnceAsync());
        await processor.PollOnceAsync(); await processor.PollOnceAsync();
        Assert.Equal(1, executed);
        Assert.Contains(api.Receipts, r => r.Status == "Accepted");
        Assert.Contains(api.Receipts, r => r.Status == "Running");
        Assert.All(api.Receipts.Where(r => r.Status == "Succeeded"), r => Assert.Equal(lastRun!.RunId, r.RunId));
    }
    [Fact]
    public async Task LostPermissionToStartIsUncertainAndNeverReplayed()
    {
        var (processor, api, _, _) = await Create(); api.LoseStartReply = true;
        await processor.PollOnceAsync(); await processor.PollOnceAsync();
        Assert.Equal(0, executed); Assert.Contains(api.Receipts, r => r.Status == "Interrupted");
    }
    [Fact]
    public async Task DeadHostStartMarkerBecomesInterruptedWithoutReplay()
    {
        var (processor, api, _, journal) = await Create(); var command = api.Commands.Single();
        await new AgentJsonFile<RemoteCommandJournal>(Path.Combine(journal, command.CommandId.ToString("N") + ".json"))
            .WriteAsync(new(command, new(command.CommandId, "Running")), default);
        await processor.PollOnceAsync();
        Assert.Equal(0, executed); Assert.Contains(api.Receipts, r => r.Status == "Interrupted" && r.Reason == "AgentRestarted");
    }
    [Fact]
    public async Task StaleRevisionAndUnknownJobNeverRun()
    {
        var (processor, api, _, _) = await Create();
        api.Commands = [api.Commands.Single() with { Revision = 2 }];
        await processor.PollOnceAsync();
        Assert.Equal(0, executed); Assert.Contains(api.Receipts, r => r.Reason == "RevisionChanged");
        api.Commands = [new(Guid.NewGuid(), Guid.NewGuid(), 3, clock.Now.AddMinutes(10))];
        await processor.PollOnceAsync();
        Assert.Equal(0, executed); Assert.Contains(api.Receipts, r => r.Reason == "JobUnavailable");
    }
    [Fact]
    public async Task CommandExpiringWhileWaitingForExecutionGateDoesNotStart()
    {
        var (processor, api, dispatcher, _) = await Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Reserve the same gate with a first command, as a schedule/manual operation does.
        var gate = dispatcher.ExecuteCommandAsync(async token => { entered.SetResult(); await release.Task; return null; }, () => null, default);
        await entered.Task;
        var queued = processor.PollOnceAsync();
        await Task.Delay(20);
        clock.Now = clock.Now.AddHours(1); release.SetResult(); await gate; await queued;
        Assert.Equal(0, executed); Assert.Contains(api.Receipts, r => r.Status == "Expired");
    }
    [Fact]
    public async Task ChangedPayloadCannotReuseAnExecutedCommandId()
    {
        var (processor, api, _, _) = await Create(); await processor.PollOnceAsync();
        api.Commands = [api.Commands.Single() with { JobId = Guid.NewGuid() }];
        await Assert.ThrowsAsync<InvalidDataException>(() => processor.PollOnceAsync()); Assert.Equal(1, executed);
    }
    [Fact]
    public async Task CorruptTombstoneCannotReplayAndDoesNotBlockOtherCommands()
    {
        var (processor, api, _, journal) = await Create(); var old = api.Commands.Single();
        Directory.CreateDirectory(journal);
        var corrupt = Path.Combine(journal, old.CommandId.ToString("N") + ".json");
        await File.WriteAllTextAsync(corrupt, "null");
        api.Commands = [old, new(Guid.NewGuid(), job.Id, 3, clock.Now.AddMinutes(10))];
        await processor.PollOnceAsync();
        Assert.Equal(1, executed); Assert.True(processor.JournalWarning);
        Assert.Equal("null", await File.ReadAllTextAsync(corrupt));
        Assert.DoesNotContain(api.Receipts, r => r.CommandId == old.CommandId);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
