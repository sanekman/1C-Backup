using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class BackupDiskSpaceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sp-capacity-" + Guid.NewGuid().ToString("N"));
    private BackupJob Job => new() { Name = "Capacity", Kind = DatabaseKind.FilesAndFolders,
        Source = Path.Combine(root, "source"), DestinationDirectory = Path.Combine(root, "copies"), KeepLast = 1 };
    public BackupDiskSpaceTests() => Directory.CreateDirectory(Job.Source);

    [Fact]
    public async Task InsufficientSpaceFailsBeforeHashOrTemporaryWriteAndPreservesSourceAndExistingCopies()
    {
        var source = Path.Combine(root, "ready.zip"); await File.WriteAllBytesAsync(source, new byte[8192]);
        Directory.CreateDirectory(Job.DestinationDirectory);
        var old = Path.Combine(Job.DestinationDirectory, "old.zip"); await File.WriteAllTextAsync(old, "keep");
        var stages = new List<BackupStage>();
        using var progress = BackupProgressContext.Begin(Job, Guid.NewGuid(), s => stages.Add(s.Progress.Stage));
        var error = await Assert.ThrowsAsync<IOException>(() => new FileBackupStorage(_ => 4096).StoreAsync(source, Job));
        Assert.Contains("Недостаточно места", error.Message);
        Assert.Contains(BackupDiskSpace.Format(8192), error.Message);
        Assert.Contains(BackupDiskSpace.Format(4096), error.Message);
        Assert.DoesNotContain(BackupStage.Verifying, stages);
        Assert.DoesNotContain(BackupStage.CopyingToStorage, stages);
        Assert.Equal("keep", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(Job.DestinationDirectory))));
        Assert.Equal(8192, new FileInfo(source).Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1L)]
    [InlineData(8192L)]
    public async Task UnknownOrSufficientCapacityStillDeliversAndVerifies(long? available)
    {
        var source = Path.Combine(root, "ready.zip"); await File.WriteAllBytesAsync(source, new byte[8192]);
        var stored = await new FileBackupStorage(_ => available).StoreAsync(source, Job);
        await new FileBackupStorage().VerifyAsync(stored, Job);
        Assert.Equal(8192, stored.Length);
        Assert.True(File.Exists(source));
        Assert.Single(Directory.GetFiles(Job.DestinationDirectory));
    }

    [Fact]
    public async Task ArchiveAlreadyInStorageRequiresNoAdditionalCapacity()
    {
        Directory.CreateDirectory(Job.DestinationDirectory);
        var path = Path.Combine(Job.DestinationDirectory, "same.zip"); await File.WriteAllTextAsync(path, "archive");
        var stored = await new FileBackupStorage(_ => throw new InvalidOperationException("No extra allocation")).StoreAsync(path, Job);
        Assert.Equal(path, stored.Location); await new FileBackupStorage().VerifyAsync(stored, Job);
    }

    [Fact]
    public async Task ExistingDestinationAndCancellationAreRejectedWithoutQueryOrOverwrite()
    {
        var source = Path.Combine(root, "ready.zip"); await File.WriteAllTextAsync(source, "new");
        Directory.CreateDirectory(Job.DestinationDirectory);
        var existing = Path.Combine(Job.DestinationDirectory, "ready.zip"); await File.WriteAllTextAsync(existing, "old");
        var storage = new FileBackupStorage(_ => throw new InvalidOperationException("Unexpected query"));
        await Assert.ThrowsAsync<IOException>(() => storage.StoreAsync(source, Job));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.StoreAsync(source, Job, stop.Token));
        Assert.Equal("old", await File.ReadAllTextAsync(existing));
    }

    [Fact]
    public async Task FullDestinationDoesNotBlockOtherDestinationsOrDeleteItsPriorBackup()
    {
        var catalog = new BackupCatalog(Path.Combine(root, "catalog.json"));
        var history = new BackupRunHistory(Path.Combine(root, "runs.json"));
        var job = Job with { AdditionalDestinations = [new() { DestinationDirectory = Path.Combine(root, "other"), KeepLast = 1 }] };
        await File.WriteAllTextAsync(Path.Combine(job.Source, "data.txt"), "original");
        var first = await new LocalBackupRunner(new Unused(), new(new FileBackupStorage(), catalog), history, Path.Combine(root, "work")).RunAsync(job);
        Assert.Equal(BackupRunStatus.Succeeded, first.Status);
        var old = (await catalog.ReadAsync()).Single(r => r.StorageIdentity == StorageAddress.Identity(job));
        var oldHash = old.Backup.Sha256;
        await File.WriteAllTextAsync(Path.Combine(job.Source, "data.txt"), "updated");
        var capacity = new FileBackupStorage(path => path == job.DestinationDirectory ? 0 : long.MaxValue);
        var second = await new LocalBackupRunner(new Unused(), new(capacity, catalog), history, Path.Combine(root, "work")).RunAsync(job);
        Assert.Equal(BackupRunStatus.PartiallySucceeded, second.Status);
        Assert.Contains("Недостаточно места", second.Error);
        Assert.Single(second.Destinations!, d => d.Record is not null);
        Assert.Single(second.Destinations!, d => d.Error is not null);
        Assert.True(Directory.Exists(second.WorkDirectory));
        Assert.NotEmpty(Directory.GetFiles(second.WorkDirectory, "*.zip"));
        var remaining = await catalog.ReadAsync(); Assert.Equal(2, remaining.Count);
        Assert.Contains(old, remaining); Assert.Equal(oldHash, old.Backup.Sha256);
        await new BackupCopyVerifier(new FileBackupStorage()).VerifyAsync(old, job);
        Assert.Equal(BackupRunStatus.PartiallySucceeded, (await history.ReadAsync()).Single(r => r.RunId == second.RunId).Status);
    }

    [Fact]
    public void RealQueryUsesExistingAncestorAndDoesNotCreateMissingFolders()
    {
        var missing = Path.Combine(root, "missing", "nested");
        Assert.True(BackupDiskSpace.AvailableBytes(missing) is >= 0);
        Assert.False(Directory.Exists(Path.Combine(root, "missing")));
        Assert.Contains("ГиБ", BackupDiskSpace.Format(3L * 1024 * 1024 * 1024));
    }

    private sealed class Unused : IBackupExecutor
    { public Task<string> CreateAsync(BackupJob job, string work, CancellationToken token) => throw new InvalidOperationException("Files executor selected by runner"); }
    public void Dispose() => Directory.Delete(root, true);
}
