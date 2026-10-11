using System.Text.Json;
using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class LatestBackupAuditTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OneCBackup-audit-" + Guid.NewGuid());
    private readonly BackupJob job;
    private readonly BackupCatalog catalog;
    public LatestBackupAuditTests()
    {
        Directory.CreateDirectory(root);
        job = new() { Id = Guid.NewGuid(), Name = "Audit", Source = "fixture",
            DestinationDirectory = Path.Combine(root, "first"),
            AdditionalDestinations = [new() { DestinationDirectory = Path.Combine(root, "second") },
                new() { DestinationDirectory = Path.Combine(root, "third") }] };
        catalog = new(Path.Combine(root, "catalog.json"));
    }
    private async Task<BackupRecord> CopyAsync(BackupJob target, string name, DateTimeOffset completed)
    {
        var source = Path.Combine(root, name + ".bak");
        await File.WriteAllTextAsync(source, "audit content " + name);
        var stored = await new FileBackupStorage().StoreAsync(source, target);
        return await catalog.RegisterAsync(target, stored, completed);
    }

    [Fact]
    public async Task ChecksNewestPerCurrentDestinationAndLeavesCatalogAndFilesUnchanged()
    {
        var targets = job.StorageJobs;
        var old = await CopyAsync(targets[0], "old", DateTimeOffset.UtcNow.AddDays(-1));
        var latest = await CopyAsync(targets[0], "latest", DateTimeOffset.UtcNow);
        var second = await CopyAsync(targets[1], "second", DateTimeOffset.UtcNow);
        await CopyAsync(targets[2] with { Id = Guid.NewGuid() }, "foreign-job", DateTimeOffset.UtcNow.AddHours(1));
        var before = await File.ReadAllTextAsync(Path.Combine(root, "catalog.json"));
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        var storage = new TrackingStorage();
        var result = await new LatestBackupAudit(storage, catalog).CheckAsync(job);
        Assert.False(result.Cancelled);
        Assert.Equal(new[] { BackupAuditStatus.Verified, BackupAuditStatus.Verified, BackupAuditStatus.Missing }, result.Destinations.Select(r => r.Status));
        Assert.Equal(new[] { latest.Id, second.Id }, storage.Records.Select(r => r.Id));
        Assert.DoesNotContain(old.Id, storage.Records.Select(r => r.Id));
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(root, "catalog.json")));
        Assert.All(files, pair => Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingOrCorruptNewestDoesNotFallBackToOldCopyOrStopOtherDestinations(bool corrupt)
    {
        var targets = job.StorageJobs;
        await CopyAsync(targets[0], "old", DateTimeOffset.UtcNow.AddDays(-1));
        var latest = await CopyAsync(targets[0], "latest", DateTimeOffset.UtcNow);
        await CopyAsync(targets[1], "second", DateTimeOffset.UtcNow);
        if (corrupt)
        {
            var bytes = await File.ReadAllBytesAsync(latest.Backup.Location);
            bytes[0] ^= 1;
            await File.WriteAllBytesAsync(latest.Backup.Location, bytes);
        }
        else File.Delete(latest.Backup.Location);
        var before = JsonSerializer.Serialize(await catalog.ReadAsync());
        var result = await new LatestBackupAudit(new TrackingStorage(), catalog).CheckAsync(job);
        Assert.Equal(BackupAuditStatus.Failed, result.Destinations[0].Status);
        Assert.Equal(latest, result.Destinations[0].Record);
        Assert.NotNull(result.Destinations[0].Error);
        Assert.Equal(BackupAuditStatus.Verified, result.Destinations[1].Status);
        Assert.Equal(BackupAuditStatus.Missing, result.Destinations[2].Status);
        Assert.Equal(before, JsonSerializer.Serialize(await catalog.ReadAsync()));
    }

    [Fact]
    public async Task RemovedStorageIsNotAccessedAndNewStorageHasNoCopy()
    {
        foreach (var target in job.StorageJobs) await CopyAsync(target, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var changed = job with { AdditionalDestinations = [new() { DestinationDirectory = Path.Combine(root, "new-place") }] };
        var storage = new TrackingStorage();
        var result = await new LatestBackupAudit(storage, catalog).CheckAsync(changed);
        Assert.Equal(2, result.Destinations.Count);
        Assert.Single(storage.Records);
        Assert.Equal(BackupAuditStatus.Verified, result.Destinations[0].Status);
        Assert.Equal(BackupAuditStatus.Missing, result.Destinations[1].Status);
    }

    [Fact]
    public async Task CancellationKeepsCompletedResultAndDoesNotReadRemainingFiles()
    {
        foreach (var target in job.StorageJobs) await CopyAsync(target, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        using var cancellation = new CancellationTokenSource();
        var storage = new TrackingStorage { AfterVerify = cancellation.Cancel };
        var result = await new LatestBackupAudit(storage, catalog).CheckAsync(job, cancellation.Token);
        Assert.True(result.Cancelled);
        Assert.Single(storage.Records);
        Assert.Equal(BackupAuditStatus.Verified, result.Destinations[0].Status);
        Assert.All(result.Destinations.Skip(1), r => Assert.Equal(BackupAuditStatus.Cancelled, r.Status));
    }

    [Fact]
    public async Task ActiveCancellationWaitsForStorageCleanupBeforeCompleting()
    {
        foreach (var target in job.StorageJobs) await CopyAsync(target, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var before = JsonSerializer.Serialize(await catalog.ReadAsync());
        using var cancellation = new CancellationTokenSource();
        var storage = new CancellingStorage();
        var task = new LatestBackupAudit(storage, catalog).CheckAsync(job, cancellation.Token);
        try
        {
            await storage.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await storage.Cancelling.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(task.IsCompleted);
            storage.Cleanup.TrySetResult();
            var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(result.Cancelled);
            Assert.All(result.Destinations, result => Assert.Equal(BackupAuditStatus.Cancelled, result.Status));
            Assert.Equal(1, storage.Reads);
            Assert.Equal(before, JsonSerializer.Serialize(await catalog.ReadAsync()));
        }
        finally { cancellation.Cancel(); storage.Cleanup.TrySetResult(); await task; }
    }

    [Fact]
    public async Task CorruptCatalogAndInvalidJobCannotAccessStorage()
    {
        var storage = new TrackingStorage();
        await File.WriteAllTextAsync(Path.Combine(root, "catalog.json"), "[null]");
        await Assert.ThrowsAsync<InvalidDataException>(() => new LatestBackupAudit(storage, catalog).CheckAsync(job));
        await Assert.ThrowsAsync<InvalidDataException>(() => new LatestBackupAudit(storage, catalog).CheckAsync(job with { KeepLast = 0 }));
        Assert.Empty(storage.Records);
    }

    private sealed class CancellingStorage : IBackupStorage
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelling { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Reads { get; private set; }
        public Task<StoredBackup> StoreAsync(string path, BackupJob target, CancellationToken token = default) => throw new InvalidOperationException();
        public Task DeleteAsync(StoredBackup backup, BackupJob target, CancellationToken token = default) => throw new InvalidOperationException();
        public async Task VerifyAsync(StoredBackup backup, BackupJob target, CancellationToken token = default)
        {
            Reads++;
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException)
            {
                Cancelling.TrySetResult();
                await Cleanup.Task;
                throw;
            }
        }
    }

    private sealed class TrackingStorage : IBackupStorage
    {
        public List<BackupRecord> Records { get; } = [];
        public Action? AfterVerify { get; init; }
        public Task<StoredBackup> StoreAsync(string path, BackupJob target, CancellationToken token = default) => throw new InvalidOperationException("Audit must not store copies");
        public Task DeleteAsync(StoredBackup backup, BackupJob target, CancellationToken token = default) => throw new InvalidOperationException("Audit must not delete copies");
        public async Task VerifyAsync(StoredBackup backup, BackupJob target, CancellationToken token = default)
        {
            // Recover the registered record for assertions; production storage only receives StoredBackup.
            var records = await new BackupCatalog(Path.Combine(Path.GetDirectoryName(target.DestinationDirectory)!, "catalog.json")).ReadAsync(token);
            Records.Add(records.Single(r => r.Backup == backup));
            await new FileBackupStorage().VerifyAsync(backup, target, token);
            AfterVerify?.Invoke();
        }
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
