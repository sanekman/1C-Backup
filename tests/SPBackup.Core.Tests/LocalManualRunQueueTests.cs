using System.Text.Json;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class LocalManualRunQueueTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OneCBackup-manual-" + Guid.NewGuid());

    [Fact]
    public async Task ReopenedQueueRecoversAcceptedRequestsButNotFinishedOrPreviousSessions()
    {
        var queue = new LocalManualRunQueue(directory);
        var session = Guid.NewGuid();
        var accepted = await queue.EnqueueAsync(session, Guid.NewGuid());
        await queue.WriteReceiptAsync(new(accepted.RequestId, session, accepted.JobId, LocalManualRunPhase.Accepted));
        queue.Consume(accepted.RequestId);
        var pending = await queue.EnqueueAsync(session, Guid.NewGuid());
        var old = await queue.EnqueueAsync(Guid.NewGuid(), Guid.NewGuid());
        await queue.WriteReceiptAsync(new(old.RequestId, old.SessionId, old.JobId, LocalManualRunPhase.Accepted));
        queue.Consume(old.RequestId);
        var reopened = new LocalManualRunQueue(directory);
        var outstanding = await reopened.OutstandingAsync(session);
        Assert.Equal(2, outstanding.Count);
        Assert.Contains(new(accepted.RequestId, session, accepted.JobId), outstanding);
        Assert.Contains(new(pending.RequestId, session, pending.JobId), outstanding);
        await queue.WriteReceiptAsync(new(accepted.RequestId, session, accepted.JobId, LocalManualRunPhase.Finished));
        Assert.Equal(pending.RequestId, Assert.Single(await reopened.OutstandingAsync(session)).RequestId);
    }

    [Fact]
    public async Task CorruptReceiptDoesNotHideOtherOutstandingRequestsAndCannotImpersonateAnotherCommand()
    {
        var queue = new LocalManualRunQueue(directory);
        var session = Guid.NewGuid();
        var accepted = await queue.EnqueueAsync(session, Guid.NewGuid());
        await queue.WriteReceiptAsync(new(accepted.RequestId, session, accepted.JobId, LocalManualRunPhase.Accepted));
        queue.Consume(accepted.RequestId);
        var invalidId = Guid.NewGuid();
        var path = Path.Combine(directory, "manual-runs", "receipts", invalidId.ToString("N") + ".json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new LocalManualRunReceipt(Guid.NewGuid(), session, Guid.NewGuid(), LocalManualRunPhase.Accepted)));
        await Assert.ThrowsAsync<InvalidDataException>(() => queue.ReadReceiptAsync(invalidId));
        await File.WriteAllTextAsync(Path.Combine(directory, "manual-runs", "receipts", Guid.NewGuid().ToString("N") + ".json"), "null");
        Assert.Equal(accepted.RequestId, Assert.Single(await queue.OutstandingAsync(session)).RequestId);
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    [Fact]
    public async Task AbandonedReceiptsPreserveBindingsAndIgnoreLiveTerminalAndCorruptReceipts()
    {
        var queue = new LocalManualRunQueue(directory);
        var liveSession = Guid.NewGuid();
        var job = Guid.NewGuid();
        var old = new LocalManualRunReceipt(Guid.NewGuid(), Guid.NewGuid(), job, LocalManualRunPhase.Accepted);
        await queue.WriteReceiptAsync(old);
        var live = old with { RequestId = Guid.NewGuid(), SessionId = liveSession };
        var done = old with { RequestId = Guid.NewGuid(), Phase = LocalManualRunPhase.Finished };
        await queue.WriteReceiptAsync(live); await queue.WriteReceiptAsync(done);
        var corrupt = Path.Combine(directory, "manual-runs", "receipts", Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(corrupt, "{broken");
        var result = Assert.Single(await queue.AbandonedAsync(liveSession));
        Assert.Equal(old.RequestId, result.RequestId);
        Assert.Equal(old.SessionId, result.SessionId);
        Assert.Equal(job, result.JobId);
        Assert.Equal(LocalManualRunPhase.Interrupted, result.Phase);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        // Discovery never writes; the exclusive host publishes through its normal retry queue.
        Assert.Equal(old, await queue.ReadReceiptAsync(old.RequestId));
        await queue.WriteReceiptAsync(result);
        Assert.Empty(await queue.AbandonedAsync(liveSession));
        Assert.Equal(live, await queue.ReadReceiptAsync(live.RequestId));
        Assert.Equal(done, await queue.ReadReceiptAsync(done.RequestId));
        Assert.Equal("{broken", await File.ReadAllTextAsync(corrupt));
    }
}
