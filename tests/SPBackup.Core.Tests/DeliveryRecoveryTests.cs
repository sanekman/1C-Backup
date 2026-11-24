using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class DeliveryRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "spbackup-delivery-recovery-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public async Task InterruptedDeliveryIsRecoveredAtMostOnceAfterCheckingStoredBytes(bool registered, bool damaged, bool hasReceipt)
    {
        Directory.CreateDirectory(root);
        var workRoot = Path.Combine(root, "work");
        var catalog = new BackupCatalog(Path.Combine(root, "copies.json"));
        var history = new BackupRunHistory(Path.Combine(root, "runs.json"));
        var storage = new FileBackupStorage();
        var job = new BackupJob { Id = Guid.NewGuid(), Name = "Recovery",
            Source = "test", DestinationDirectory = Path.Combine(root, "destination") };
        var runId = Guid.NewGuid();
        var work = Path.Combine(workRoot, runId.ToString("N"));
        var staging = Path.Combine(work, "archive");
        var now = DateTimeOffset.UtcNow;
        await history.SaveAsync(new(runId, job.Id, job.Name, now, null, BackupRunStatus.Running, work));
        BackupDeliveryJournal.Prepare(work, runId);
        await BackupDeliveryJournal.WriteAsync(staging, runId, job, 0, null, "known.dt");
        var source = Path.Combine(root, "known.dt");
        await File.WriteAllTextAsync(source, "known archive");
        var backup = await storage.StoreAsync(source, job);
        var record = BackupCatalog.CreateRecord(job, backup, now);
        if (hasReceipt) await BackupDeliveryJournal.WriteAsync(staging, runId, job, 0, record);
        if (registered) await catalog.RegisterAsync(job, record, default);
        if (damaged) await File.AppendAllTextAsync(backup.Location, " damaged");
        var unrelated = Path.Combine(job.DestinationDirectory, "unknown.dt");
        await File.WriteAllTextAsync(unrelated, "unrelated");

        await history.RecoverInterruptedDeliveriesAsync([job], workRoot, catalog, storage);
        await history.RecoverInterruptedDeliveriesAsync([job], workRoot, catalog, storage);

        var result = Assert.Single(await history.ReadAsync());
        Assert.Equal(BackupRunStatus.Interrupted, result.Status);
        var destination = Assert.Single(result.Destinations!);
        var entries = await catalog.ReadAsync();
        if (damaged || !hasReceipt)
        {
            Assert.Empty(entries);
            Assert.Null(destination.Record);
            Assert.Contains(damaged ? backup.Location : "known.dt", destination.Error);
        }
        else
        {
            Assert.Equal(record, Assert.Single(entries));
            Assert.Equal(record, destination.Record);
        }
        Assert.True(File.Exists(backup.Location));
        Assert.Equal("unrelated", await File.ReadAllTextAsync(unrelated));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
