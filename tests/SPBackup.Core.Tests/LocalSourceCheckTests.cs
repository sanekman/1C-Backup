using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class LocalSourceCheckTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sp-source-probe-" + Guid.NewGuid().ToString("N"));
    private readonly Guid session = Guid.NewGuid();
    private readonly CancellationTokenSource stop = new();
    private Task? loop;
    private BackupJob job = null!;
    private LocalSourceCheckQueue Queue => new(root);
    private JobStore Jobs => new(Path.Combine(root, "jobs.json"), useLocalTime: true);
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        job = new() { Name = "Источник", Kind = DatabaseKind.FilesAndFolders, Source = Path.Combine(root, "source.txt"),
            DestinationDirectory = Path.Combine(root, "copies"), Enabled = false, ScheduleKind = ScheduleKind.Manual };
        await File.WriteAllTextAsync(job.Source, "source contents");
        await Jobs.SaveAsync([job]);
    }
    private async Task Start()
    {
        var now = DateTimeOffset.UtcNow;
        await new LocalAgentControlStore(root).WriteAsync(new(session, Environment.ProcessId, now, now, LocalAgentPhase.Running) { CanCheckSources = true });
        loop = new LocalSourceCheckProcessor(root, Jobs, new(new BackupProcessRunner(), Path.Combine(root, "work")))
            .RunAsync(session, _ => { }, stop.Token);
    }
    private async Task<LocalSourceCheckReceipt> Finish(LocalSourceCheckRequest request)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var receipt = await Queue.ReadAsync(request, deadline.Token);
            if (receipt is { Phase: not LocalSourceCheckPhase.Accepted }) return receipt;
            await Task.Delay(20, deadline.Token);
        }
    }
    [Fact]
    public async Task HostChecksRealSourceWithIdentityAndTimesWithoutBackupOrProfileChanges()
    {
        await Start();
        using var lease = new BackupEngineCoordinator(root).Acquire();
        await File.WriteAllTextAsync(Path.Combine(root, "runs.json"), "history marker");
        await File.WriteAllTextAsync(Path.Combine(root, "copies.json"), "catalog marker");
        var saved = await File.ReadAllBytesAsync(Path.Combine(root, "jobs.json"));
        var request = await Queue.EnqueueAsync(session, job);
        var report = await Queue.WaitAsync(request);
        Assert.True(report.Succeeded); Assert.False(report.Cancelled);
        Assert.Equal(StorageCheckIdentity.Current(), report.ExecutionIdentity);
        Assert.NotNull(report.StartedUtc); Assert.True(report.FinishedUtc >= report.StartedUtc);
        Assert.Equal("source contents", await File.ReadAllTextAsync(job.Source));
        Assert.Equal(saved, await File.ReadAllBytesAsync(Path.Combine(root, "jobs.json")));
        Assert.Equal("history marker", await File.ReadAllTextAsync(Path.Combine(root, "runs.json")));
        Assert.Equal("catalog marker", await File.ReadAllTextAsync(Path.Combine(root, "copies.json")));
        Assert.False(Directory.Exists(job.DestinationDirectory));
        Assert.Empty(await new LocalManualRunQueue(root).PendingAsync());
    }
    [Fact]
    public async Task SourceFailureDoesNotPreventNextProbeAndNeverCreatesABackup()
    {
        await Start(); File.Delete(job.Source);
        Assert.False((await Queue.WaitAsync(await Queue.EnqueueAsync(session, job))).Succeeded);
        await File.WriteAllTextAsync(job.Source, "recovered");
        Assert.True((await Queue.WaitAsync(await Queue.EnqueueAsync(session, job))).Succeeded);
        Assert.False(Directory.Exists(job.DestinationDirectory));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StaleSessionOrChangedSettingsCannotProbeSavedSource(bool wrongSession)
    {
        var request = await Queue.EnqueueAsync(wrongSession ? Guid.NewGuid() : session,
            wrongSession ? job : job with { Source = job.Source + "-changed" });
        await Start();
        var receipt = await Finish(request);
        Assert.Equal(LocalSourceCheckPhase.Rejected, receipt.Phase); Assert.Null(receipt.Report);
    }
    [Fact]
    public async Task CancelBeforeProcessingNeverReportsSourceSuccess()
    {
        var request = await Queue.EnqueueAsync(session, job);
        await Queue.CancelAsync(request); await Start();
        var receipt = await Finish(request);
        Assert.Equal(LocalSourceCheckPhase.Cancelled, receipt.Phase); Assert.Null(receipt.Report);
    }
    [Fact]
    public async Task RedeliveryOfCompletedRequestDoesNotReadSourceAgain()
    {
        await Start(); var request = await Queue.EnqueueAsync(session, job);
        Assert.True((await Queue.WaitAsync(request)).Succeeded);
        File.Delete(job.Source);
        var path = Path.Combine(root, "source-checks", "pending", request.RequestId.ToString("N") + ".json");
        await new AgentJsonFile<LocalSourceCheckRequest>(path).WriteAsync(request, CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (File.Exists(path)) await Task.Delay(20, deadline.Token);
        Assert.True((await Queue.ReadAsync(request))!.Report!.Succeeded);
    }
    [Fact]
    public async Task WrongReceiptOrMissingIdentityIsNotAConfirmedProbe()
    {
        var request = await Queue.EnqueueAsync(session, job);
        var good = new SourceCheckReport(true, "read") { ExecutionIdentity = StorageCheckIdentity.Current(), StartedUtc = DateTimeOffset.UtcNow, FinishedUtc = DateTimeOffset.UtcNow };
        foreach (var receipt in new[] {
            new LocalSourceCheckReceipt(request.RequestId, Guid.NewGuid(), job.Id, request.JobFingerprint, LocalSourceCheckPhase.Completed, good),
            new LocalSourceCheckReceipt(request.RequestId, session, job.Id, request.JobFingerprint, LocalSourceCheckPhase.Completed, good with { ExecutionIdentity = null }),
            new LocalSourceCheckReceipt(request.RequestId, session, job.Id, request.JobFingerprint, LocalSourceCheckPhase.Completed, good with { FinishedUtc = null }) })
        {
            await Queue.WriteAsync(receipt, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidDataException>(() => Queue.ReadAsync(request));
        }
    }
    [Fact]
    public void OlderOrStaleHostIsNeverAdvertisedAsSupportingSourceChecks()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new LocalAgentState(session, Environment.ProcessId, now.AddMinutes(-1), now, LocalAgentPhase.Running) { CanCheckStorages = true };
        Assert.False(LocalSourceCheckQueue.IsAvailable(state, now));
        Assert.True(LocalSourceCheckQueue.IsAvailable(state with { CanCheckSources = true }, now));
        Assert.False(LocalSourceCheckQueue.IsAvailable(state with { CanCheckSources = true, UpdatedUtc = now.AddSeconds(-15) }, now));
        Assert.False(LocalSourceCheckQueue.IsAvailable(state with { CanCheckSources = true, Phase = LocalAgentPhase.Stopped }, now));
    }
    public async Task DisposeAsync()
    {
        stop.Cancel();
        if (loop is not null) { try { await loop; } catch (OperationCanceledException) { } }
        stop.Dispose(); Directory.Delete(root, true);
    }
}
