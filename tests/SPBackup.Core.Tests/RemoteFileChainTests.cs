using System.Security.Cryptography;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class RemoteFileChainTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sp-remote-chain-" + Guid.NewGuid().ToString("N"));
    private readonly MemoryRemote storage = new();
    private readonly BackupCatalog catalog;
    private readonly LocalBackupRunner runner;
    private readonly BackupJob job;
    private string Payload => Path.Combine(root, "source", "payload.txt");
    private string Cache => Path.Combine(root, "file-snapshots", job.Id.ToString("N") + ".json");
    public RemoteFileChainTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "source"));
        catalog = new(Path.Combine(root, "copies.json"));
        runner = new(new NoDatabase(), new(storage, catalog), new(Path.Combine(root, "runs.json")), Path.Combine(root, "work"));
        job = new() { Name = "Remote chain", Kind = DatabaseKind.FilesAndFolders, Source = Path.GetDirectoryName(Payload)!,
            StorageKind = StorageKind.Ftp, Ftp = new() { Host = "fixture.invalid", CredentialKey = "fixture" },
            DestinationDirectory = "/first", KeepLast = 20, FileBackupMode = FileBackupMode.Incremental, FullBackupEvery = 7 };
    }

    [Fact]
    public async Task LostCacheRecoversFromVerifiedRemoteArchivesWithoutAnotherFullCopy()
    {
        await File.WriteAllTextAsync(Payload, "first"); var full = await runner.RunAsync(job);
        await File.WriteAllTextAsync(Payload, "second"); var second = await runner.RunAsync(job);
        Assert.Equal(BackupRunStatus.Succeeded, full.Status); Assert.Equal(BackupRunStatus.Succeeded, second.Status);
        File.Delete(Cache); await File.WriteAllTextAsync(Payload, "third");
        var third = await runner.RunAsync(job);
        Assert.Equal(BackupRunStatus.Succeeded, third.Status);
        Assert.False(third.Backup!.FileSnapshot!.IsFull);
        Assert.Equal(full.Backup!.FileSnapshot!.SnapshotId, third.Backup.FileSnapshot.FullSnapshotId);
        Assert.Equal(second.Backup!.FileSnapshot!.SnapshotId, third.Backup.FileSnapshot.ParentSnapshotId);
        Assert.True(storage.Downloads >= 2); Assert.True(File.Exists(Cache));
        await AssertRestores((await catalog.ReadAsync()).Last(), job, "third");
        Assert.False(Directory.Exists(third.WorkDirectory));
    }

    [Fact]
    public async Task CorruptRemoteRecoveryFailsWithoutPublishingOrDeletingAndCanRecoverAfterAccessReturns()
    {
        await File.WriteAllTextAsync(Payload, "first"); var full = await runner.RunAsync(job);
        Assert.Equal(BackupRunStatus.Succeeded, full.Status); File.Delete(Cache);
        var before = storage.Bytes.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        storage.CorruptDownload = true; await File.WriteAllTextAsync(Payload, "second");
        var failed = await runner.RunAsync(job);
        Assert.Equal(BackupRunStatus.Failed, failed.Status); Assert.Null(failed.Backup);
        Assert.Single(await catalog.ReadAsync()); Assert.Single(storage.Bytes);
        Assert.Equal(before.Single().Value, storage.Bytes.Single().Value);
        storage.CorruptDownload = false;
        var recovered = await runner.RunAsync(job);
        Assert.Equal(BackupRunStatus.Succeeded, recovered.Status); Assert.False(recovered.Backup!.FileSnapshot!.IsFull);
        await AssertRestores((await catalog.ReadAsync()).Last(), job, "second");
    }

    [Fact]
    public async Task LaggingRemoteMirrorReceivesAncestorsAndBothMirrorsRestoreAfterAnOutage()
    {
        var mirrored = job with { AdditionalDestinations = [new() { StorageKind = StorageKind.Ftp, Ftp = job.Ftp, DestinationDirectory = "/second", KeepLast = 20 }] };
        await File.WriteAllTextAsync(Payload, "first"); Assert.Equal(BackupRunStatus.Succeeded, (await runner.RunAsync(mirrored)).Status);
        storage.UnavailableDirectory = "/second"; await File.WriteAllTextAsync(Payload, "second");
        var partial = await runner.RunAsync(mirrored); Assert.Equal(BackupRunStatus.PartiallySucceeded, partial.Status);
        storage.UnavailableDirectory = null; File.Delete(Cache); await File.WriteAllTextAsync(Payload, "third");
        var recovered = await runner.RunAsync(mirrored); Assert.Equal(BackupRunStatus.Succeeded, recovered.Status);
        Assert.All(recovered.Destinations!, delivery => Assert.False(delivery.Record!.Backup.FileSnapshot!.IsFull));
        foreach (var delivery in recovered.Destinations!) await AssertRestores(delivery.Record!, mirrored, "third");
        var records = await catalog.ReadAsync();
        Assert.Equal(6, records.Count); Assert.Equal(2, records.Count(record => record.Backup.FileSnapshot!.IsFull));
        Assert.Contains(recovered.Warnings!, warning => warning.Contains("Доставлены пропущенные"));
    }

    private async Task AssertRestores(BackupRecord record, BackupJob selected, string expected)
    {
        var parent = Path.Combine(root, "restores"); Directory.CreateDirectory(parent);
        var restored = await new FileBackupRestorer(storage, catalog, Path.Combine(root, "restore-work")).RestoreAsync(record, selected, parent);
        Assert.Equal(expected, await File.ReadAllTextAsync(Path.Combine(restored.Directory, "payload.txt")));
    }
    public void Dispose() => Directory.Delete(root, true);
    private sealed class NoDatabase : IBackupExecutor
    {
        public Task<string> CreateAsync(BackupJob job, string work, CancellationToken token) => throw new InvalidOperationException();
    }
    private sealed class MemoryRemote : IBackupStorage
    {
        internal Dictionary<string, byte[]> Bytes { get; } = [];
        internal bool CorruptDownload { get; set; }
        internal string? UnavailableDirectory { get; set; }
        internal int Downloads { get; private set; }
        public async Task<StoredBackup> StoreAsync(string path, BackupJob job, CancellationToken cancellationToken = default)
        {
            if (job.DestinationDirectory == UnavailableDirectory) throw new IOException("Owned fixture remote outage");
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var location = new UriBuilder(job.Ftp!.UseTls ? "ftps" : "ftp", job.Ftp.Host, job.Ftp.Port, job.DestinationDirectory + "/" + Path.GetFileName(path)).Uri.AbsoluteUri;
            lock (Bytes) { if (!Bytes.TryAdd(location, bytes)) throw new IOException("Existing object must not be overwritten"); }
            return new(location, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        public async Task CopyToAsync(StoredBackup backup, BackupJob job, Stream destination, CancellationToken cancellationToken = default)
        {
            if (job.DestinationDirectory == UnavailableDirectory) throw new IOException("Owned fixture remote outage");
            byte[] bytes; lock (Bytes) { Downloads++; bytes = Bytes[backup.Location].ToArray(); }
            if (CorruptDownload) bytes[^1] ^= 0xff;
            await destination.WriteAsync(bytes, cancellationToken);
        }
        public Task DeleteAsync(StoredBackup backup, BackupJob job, CancellationToken cancellationToken = default)
        { lock (Bytes) Bytes.Remove(backup.Location); return Task.CompletedTask; }
        public Task VerifyAsync(StoredBackup backup, BackupJob job, CancellationToken cancellationToken = default)
        {
            byte[] bytes; lock (Bytes) bytes = Bytes[backup.Location];
            if (bytes.Length != backup.Length || Convert.ToHexString(SHA256.HashData(bytes)) != backup.Sha256) throw new InvalidDataException("Fixture corruption");
            return Task.CompletedTask;
        }
    }
}
