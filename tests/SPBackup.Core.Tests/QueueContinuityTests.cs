using System.Net;
using System.Security.Cryptography;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class QueueContinuityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sp-queue-continuity-" + Guid.NewGuid().ToString("N"));
    public QueueContinuityTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WarningOrPartialDeliveryDoesNotStopNextJobOrReplayCompletedStartupAfterRestart(bool partialDelivery)
    {
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "1Cv8.1CD"), "fixture data");
        var first = new BackupJob { Name = "First", Source = source, OneCMethod = OneCBackupMethod.FileArchive,
            DestinationDirectory = Path.Combine(root, "first"), ScheduleKind = ScheduleKind.SystemStartup };
        if (partialDelivery) first = first with { AdditionalDestinations = [new() { StorageKind = StorageKind.Ftp,
            DestinationDirectory = "/unavailable", Ftp = new() { Host = "fixture.invalid", CredentialKey = "unavailable" } }] };
        var second = first with { Id = Guid.NewGuid(), Name = "Second", DestinationDirectory = Path.Combine(root, "second"), AdditionalDestinations = null };
        await new JobStore(Path.Combine(root, "jobs.json")).SaveAsync([first, second]);
        var executor = new WarningExecutor(first.Id, !partialDelivery);
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = 0;
        using (var stop = new CancellationTokenSource())
        {
            var running = new LocalAgentHost(root, executor, new MissingCredentials()).RunAsync(pollInterval: TimeSpan.FromMilliseconds(25),
                report: run => { if (Interlocked.Increment(ref completed) == 2) both.TrySetResult(); return Task.CompletedTask; }, cancellationToken: stop.Token);
            try { await both.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
            finally { stop.Cancel(); Assert.Equal(0, await running.WaitAsync(TimeSpan.FromSeconds(10))); }
        }
        var history = new BackupRunHistory(Path.Combine(root, "runs.json"));
        var beforeRuns = await history.ReadAsync(); Assert.Equal(2, beforeRuns.Count);
        var firstRun = Assert.Single(beforeRuns, run => run.JobId == first.Id);
        Assert.Equal(partialDelivery ? BackupRunStatus.PartiallySucceeded : BackupRunStatus.Succeeded, firstRun.Status);
        if (!partialDelivery) Assert.Contains("fixture source warning", firstRun.Warnings!);
        Assert.Equal(BackupRunStatus.Succeeded, Assert.Single(beforeRuns, run => run.JobId == second.Id).Status);
        var catalog = new BackupCatalog(Path.Combine(root, "copies.json"));
        var beforeCopies = await catalog.ReadAsync(); Assert.Equal(2, beforeCopies.Count);
        var hashes = new Dictionary<string, string>();
        foreach (var copy in beforeCopies) hashes.Add(copy.Backup.Location, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(copy.Backup.Location))));
        using (var stop = new CancellationTokenSource())
        {
            var running = new LocalAgentHost(root, executor, new MissingCredentials()).RunAsync(pollInterval: TimeSpan.FromMilliseconds(25), cancellationToken: stop.Token);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var control = new LocalAgentControlStore(root); LocalAgentState? state;
                do { await Task.Delay(25, timeout.Token); state = await control.ReadAsync(timeout.Token); }
                while (state is not { Phase: LocalAgentPhase.Running, CanRunManually: true });
                var queue = new LocalManualRunQueue(root); var request = await queue.EnqueueAsync(state.SessionId, second.Id, timeout.Token);
                while ((await queue.ReadReceiptAsync(request.RequestId, timeout.Token))?.Phase != LocalManualRunPhase.Finished)
                    await Task.Delay(25, timeout.Token);
                var after = await history.ReadAsync(); Assert.Equal(3, after.Count);
                Assert.Single(after, run => run.JobId == first.Id);
                Assert.Equal(2, after.Count(run => run.JobId == second.Id));
                foreach (var pair in hashes) Assert.Equal(pair.Value, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(pair.Key))));
            }
            finally { stop.Cancel(); Assert.Equal(0, await running.WaitAsync(TimeSpan.FromSeconds(10))); }
        }
    }

    private sealed class WarningExecutor(Guid first, bool reportWarning) : IBackupExecutor
    {
        private readonly DatabaseBackupExecutor actual = new(new BackupProcessRunner());
        public Task<string> CreateAsync(BackupJob job, string work, CancellationToken token)
        {
            if (reportWarning && job.Id == first) BackupWarningContext.Report("fixture source warning");
            return actual.CreateAsync(job, work, token);
        }
    }
    private sealed class MissingCredentials : IFtpCredentialStore
    {
        public Task<NetworkCredential> GetAsync(string key, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Owned fixture unavailable credentials");
    }
    public void Dispose() => Directory.Delete(root, true);
}
