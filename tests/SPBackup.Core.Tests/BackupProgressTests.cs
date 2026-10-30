using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class BackupProgressTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OneCBackup-progress-" + Guid.NewGuid());
    private BackupJob Job => new() { Id = Guid.NewGuid(), Name = "Тест", Source = "test", DestinationDirectory = Path.Combine(root, "copies"),
        AdditionalDestinations = [new() { DestinationDirectory = Path.Combine(root, "second") }] };

    [Theory]
    [InlineData(null)]
    [InlineData("strong-password-123")]
    public async Task ArchiveReportsRealBytesAndVerificationBeforeTerminalSuccess(string? password)
    {
        Directory.CreateDirectory(root);
        var bytes = new byte[300000]; new Random(19).NextBytes(bytes);
        var events = new List<BackupProgressSnapshot>();
        using (BackupProgressContext.Begin(Job, Guid.NewGuid(), events.Add))
        {
            await new BackupArchiver().CreateZipAsync(new MemoryStream(bytes), "database", Path.Combine(root, "copy.zip"), password: password);
            BackupProgressContext.Report(BackupStage.Completed);
        }
        var archive = events.Where(e => e.Progress.Stage == BackupStage.Archiving).ToArray();
        Assert.NotEmpty(archive);
        Assert.All(archive, e => Assert.Equal(bytes.Length, e.Progress.Total));
        Assert.Equal(bytes.Length, archive[^1].Progress.Completed);
        Assert.Equal(100, archive[^1].Progress.Percent);
        Assert.Contains(events, e => e.Progress.Stage == BackupStage.Verifying && e.Progress.Percent is null);
        Assert.Equal(BackupStage.Completed, events[^1].Progress.Stage);
        Assert.Equal(100, events[^1].Progress.Percent);
    }

    [Fact]
    public async Task ConcurrentDeliveriesKeepIndependentSlotsAndFailedDestinationIsNot100Percent()
    {
        var events = new List<BackupProgressSnapshot>();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (BackupProgressContext.Begin(Job, Guid.NewGuid(), events.Add))
        {
            var first = Task.Run(async () =>
            {
                using var destination = BackupProgressContext.Destination(1);
                BackupProgressContext.Report(BackupStage.CopyingToStorage, 25, 100);
                reached.SetResult(); await Task.Delay(30);
                BackupProgressContext.Report(BackupStage.Completed);
            });
            var second = Task.Run(async () =>
            {
                await reached.Task;
                using var destination = BackupProgressContext.Destination(2);
                BackupProgressContext.Report(BackupStage.CopyingToStorage, 70, 100);
                BackupProgressContext.Report(BackupStage.Failed);
            });
            await Task.WhenAll(first, second);
            BackupProgressContext.Report(BackupStage.PartiallySucceeded);
        }
        var last = events[^1];
        Assert.Equal(BackupStage.PartiallySucceeded, last.Progress.Stage);
        Assert.Null(last.Progress.Percent);
        Assert.Equal(100, last.Destinations[0].Progress.Percent);
        Assert.Equal(BackupStage.Failed, last.Destinations[1].Progress.Stage);
        Assert.Null(last.Destinations[1].Progress.Percent);
        Assert.Equal(events.Count, events.Select(e => e.Sequence).Distinct().Count());
    }

    [Fact]
    public async Task FileTransferSnapshotCanBeReadByAnotherStoreAndObserverFailureDoesNotBreakCopy()
    {
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.bak");
        var bytes = new byte[300000]; new Random(20).NextBytes(bytes); await File.WriteAllBytesAsync(source, bytes);
        var events = new List<BackupProgressSnapshot>();
        var store = new BackupProgressStore(root);
        using (BackupProgressContext.Begin(Job, Guid.NewGuid(), value => { events.Add(value); store.Write(value); throw new IOException("observer failed"); }))
        {
            var result = await new FileBackupStorage().StoreAsync(source, Job);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(result.Location));
            BackupProgressContext.Report(BackupStage.Completed);
        }
        Assert.Contains(events, e => e.Progress.Stage == BackupStage.CopyingToStorage && e.Progress.Total == bytes.Length && e.Progress.Completed == bytes.Length);
        var observed = await new BackupProgressStore(root).ReadAsync();
        Assert.Equal(events[^1].RunId, observed!.RunId);
        Assert.True(observed.Finished);
        Assert.Equal(BackupStage.Completed, observed.Progress.Stage);
        await File.WriteAllTextAsync(Path.Combine(root, "backup-progress.json"), "broken");
        Assert.Null(await store.ReadAsync());
    }

    [Fact]
    public async Task CancellationDoesNotReportSuccessOrLeaveTemporaryArchive()
    {
        Directory.CreateDirectory(root);
        var events = new List<BackupProgressSnapshot>(); using var cancellation = new CancellationTokenSource();
        using (BackupProgressContext.Begin(Job, Guid.NewGuid(), events.Add))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BackupArchiver().CreateZipAsync(
                new MemoryStream(new byte[300000]), "data", Path.Combine(root, "cancel.zip"),
                new InlineProgress<long>(_ => cancellation.Cancel()), cancellation.Token));
            BackupProgressContext.Report(BackupStage.Cancelled);
        }
        Assert.Equal(BackupStage.Cancelled, events[^1].Progress.Stage);
        Assert.Null(events[^1].Progress.Percent);
        Assert.Empty(Directory.GetFiles(root));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
