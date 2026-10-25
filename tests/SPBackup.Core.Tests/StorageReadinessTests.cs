using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class StorageReadinessTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "onec-readiness-" + Guid.NewGuid());
    private BackupJob Job => new() { Name = "Readiness", Source = "database", KeepLast = 1,
        TemporaryDirectory = Path.Combine(root, "HDD temporary"), DestinationDirectory = Path.Combine(root, "first"),
        AdditionalDestinations = [new BackupDestination { DestinationDirectory = Path.Combine(root, "second"), KeepLast = 2 }] };
    public StorageReadinessTests() => Directory.CreateDirectory(root);
    private StorageReadinessChecker Checker(IBackupStorage? storage = null) => new(storage ?? new FileBackupStorage(), Path.Combine(root, "default-work"));

    [Fact]
    public async Task RealProbeChecksEveryDestinationAndLeavesExistingCopiesCatalogAndSourceUntouched()
    {
        var markers = new[] { Job.TemporaryDirectory!, Job.DestinationDirectory, Job.AdditionalDestinations![0].DestinationDirectory };
        foreach (var directory in markers)
        { Directory.CreateDirectory(directory); await File.WriteAllTextAsync(Path.Combine(directory, "unrelated.bak"), "keep this"); }
        var catalog = Path.Combine(root, "copies.json");
        await File.WriteAllTextAsync(catalog, "catalog marker");
        var source = Path.Combine(root, "database");
        await File.WriteAllTextAsync(source, "source bytes");
        var report = await Checker().CheckAsync(Job with { Source = source });
        Assert.Null(report.TemporaryError);
        Assert.False(report.Cancelled);
        Assert.Equal(Job.TemporaryDirectory, report.TemporaryDirectory);
        Assert.Equal(2, report.Destinations.Count);
        Assert.All(report.Destinations, result => Assert.True(result.Succeeded, result.Error));
        foreach (var directory in markers)
        {
            Assert.Empty(Directory.GetDirectories(directory));
            Assert.Equal("keep this", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(directory))));
        }
        Assert.Equal("catalog marker", await File.ReadAllTextAsync(catalog));
        Assert.Equal("source bytes", await File.ReadAllTextAsync(source));
        Assert.False(Directory.Exists(Path.Combine(root, "default-work")));
    }

    [Fact]
    public async Task OneUnavailableDestinationDoesNotPreventCheckingOthers()
    {
        await File.WriteAllTextAsync(Job.DestinationDirectory, "blocking file");
        var report = await Checker().CheckAsync(Job);
        Assert.Null(report.TemporaryError);
        Assert.False(report.Destinations[0].Succeeded);
        Assert.NotNull(report.Destinations[0].Error);
        Assert.True(report.Destinations[1].Succeeded);
        Assert.Equal("blocking file", await File.ReadAllTextAsync(Job.DestinationDirectory));
        Assert.Empty(Directory.GetFiles(Job.AdditionalDestinations![0].DestinationDirectory));
        Assert.Empty(Directory.GetDirectories(Job.TemporaryDirectory!));
    }

    [Fact]
    public async Task UnavailableTemporaryFolderStopsBeforeStorageAndDoesNotFallBack()
    {
        await File.WriteAllTextAsync(Job.TemporaryDirectory!, "blocking temporary file");
        var storage = new TrackingStorage();
        var report = await Checker(storage).CheckAsync(Job);
        Assert.NotNull(report.TemporaryError);
        Assert.Empty(report.Destinations);
        Assert.Equal(0, storage.Stores);
        Assert.False(Directory.Exists(Path.Combine(root, "default-work")));
        Assert.Equal("blocking temporary file", await File.ReadAllTextAsync(Job.TemporaryDirectory!));
    }

    [Fact]
    public async Task LegacyJobChecksDefaultTemporaryFolder()
    {
        var report = await Checker().CheckAsync(Job with { TemporaryDirectory = null });
        Assert.Equal(Path.Combine(root, "default-work"), report.TemporaryDirectory);
        Assert.Null(report.TemporaryError);
        Assert.All(report.Destinations, result => Assert.True(result.Succeeded));
        Assert.Empty(Directory.GetFileSystemEntries(report.TemporaryDirectory));
    }

    [Fact]
    public async Task CancellationAfterDeliveryStillDeletesItsProbeAndSkipsRemainingDestinations()
    {
        using var cancellation = new CancellationTokenSource();
        var storage = new TrackingStorage { AfterStore = () => cancellation.Cancel() };
        var report = await Checker(storage).CheckAsync(Job, cancellation.Token);
        Assert.True(report.Cancelled);
        Assert.Equal(1, storage.Stores);
        Assert.Equal(1, storage.Deletes);
        Assert.Equal(2, report.Destinations.Count);
        Assert.All(report.Destinations, result => Assert.False(result.Succeeded));
        Assert.Empty(Directory.GetFiles(Job.DestinationDirectory));
        Assert.False(Directory.Exists(Job.AdditionalDestinations![0].DestinationDirectory));
        Assert.Empty(Directory.GetDirectories(Job.TemporaryDirectory!));
    }

    [Fact]
    public async Task CorruptedProbeIsReportedAndNotDeletedAsAnUnchangedOwnedFile()
    {
        var storage = new TrackingStorage { Corrupt = true };
        var report = await Checker(storage).CheckAsync(Job);
        Assert.All(report.Destinations, result =>
        {
            Assert.False(result.Succeeded);
            Assert.Contains("Не удалось удалить тестовый файл", result.Error);
        });
        foreach (var path in new[] { Job.DestinationDirectory, Job.AdditionalDestinations![0].DestinationDirectory })
            Assert.Equal("changed", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(path))));
        Assert.Empty(Directory.GetDirectories(Job.TemporaryDirectory!));
    }

    [Fact]
    public async Task InvalidSettingsCannotStartAProbe()
    {
        var storage = new TrackingStorage();
        await Assert.ThrowsAsync<InvalidDataException>(() => Checker(storage).CheckAsync(Job with { KeepLast = 0 }));
        Assert.Equal(0, storage.Stores);
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public async Task PreCancelledCheckCannotCreateFoldersOrFiles()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Checker().CheckAsync(Job, cancellation.Token));
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    private sealed class TrackingStorage : IBackupStorage
    {
        private readonly FileBackupStorage files = new();
        public int Stores { get; private set; }
        public int Deletes { get; private set; }
        public Action? AfterStore { get; init; }
        public bool Corrupt { get; init; }
        public async Task<StoredBackup> StoreAsync(string path, BackupJob job, CancellationToken token)
        {
            Stores++;
            var stored = await files.StoreAsync(path, job, token);
            if (Corrupt) await File.WriteAllTextAsync(stored.Location, "changed", CancellationToken.None);
            AfterStore?.Invoke();
            return stored;
        }
        public Task VerifyAsync(StoredBackup backup, BackupJob job, CancellationToken token) => files.VerifyAsync(backup, job, token);
        public Task DeleteAsync(StoredBackup backup, BackupJob job, CancellationToken token)
        { Deletes++; return files.DeleteAsync(backup, job, token); }
    }
    public void Dispose() => Directory.Delete(root, recursive: true);
}
