using System.IO.Compression;
using System.Text.Json;
using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class FileFolderBackupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "onec-file-folders-" + Guid.NewGuid().ToString("N"));
    private readonly BackupCatalog catalog;
    private readonly BackupRunHistory history;
    private readonly FileBackupStorage storage = new();
    private readonly Guid jobId = Guid.NewGuid();
    private sealed class NoDatabase : IBackupExecutor
    { public Task<string> CreateAsync(BackupJob job, string work, CancellationToken token) => throw new InvalidOperationException("A file job must not call a database executor"); }
    private sealed class Passwords : IArchivePasswordStore
    { public Task<string> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult("fixture архив AES password"); }
    public FileFolderBackupTests()
    {
        Directory.CreateDirectory(root); Directory.CreateDirectory(Source); Directory.CreateDirectory(RestoreParent);
        catalog = new(Path.Combine(root, "copies.json")); history = new(Path.Combine(root, "runs.json"));
    }
    private string Source => Path.Combine(root, "source");
    private string RestoreParent => Path.Combine(root, "restores");
    private BackupJob Job => new() { Id = jobId, Name = "files", Kind = DatabaseKind.FilesAndFolders, Source = Source,
        DestinationDirectory = Path.Combine(root, "copies"), TemporaryDirectory = Path.Combine(root, "hdd-work"),
        KeepLast = 1, ScheduleKind = ScheduleKind.Manual, Enabled = false, FileBackupMode = FileBackupMode.Incremental,
        FullBackupEvery = 3 };
    private LocalBackupRunner Runner(IArchivePasswordStore? passwords = null) => new(new NoDatabase(),
        new BackupCompletion(storage, catalog, passwords), history, Path.Combine(root, "default-work"));
    private FileBackupRestorer Restorer(IArchivePasswordStore? passwords = null) => new(storage, catalog, Path.Combine(root, "default-work"), passwords);
    private async Task<BackupRecord> Run(BackupJob? job = null, IArchivePasswordStore? passwords = null)
    {
        var run = await Runner(passwords).RunAsync(job ?? Job); Assert.True(run.Status == BackupRunStatus.Succeeded, run.Error);
        Assert.False(Directory.Exists(run.WorkDirectory));
        return (await catalog.ReadAsync()).Single(record => record.Backup == run.Backup);
    }

    [Fact]
    public async Task FirstIncrementalIsFullAndSubsequentChangesDeletesAndEmptyFoldersRestoreExactly()
    {
        await File.WriteAllTextAsync(Path.Combine(Source, "stable.txt"), "unchanged");
        await File.WriteAllTextAsync(Path.Combine(Source, "remove.txt"), "removed after baseline");
        Directory.CreateDirectory(Path.Combine(Source, "empty")); Directory.CreateDirectory(Path.Combine(Source, "nested"));
        var changed = Path.Combine(Source, "nested", "данные.txt"); await File.WriteAllTextAsync(changed, "before");
        var full = await Run(); Assert.True(full.Backup.FileSnapshot!.IsFull);
        File.Delete(Path.Combine(Source, "remove.txt")); await File.WriteAllTextAsync(changed, "after");
        await File.WriteAllTextAsync(Path.Combine(Source, "new.txt"), "added");
        var increment = await Run(); Assert.Equal(full.Backup.FileSnapshot.SnapshotId, increment.Backup.FileSnapshot!.ParentSnapshotId);
        using (var zip = ZipFile.OpenRead(increment.Backup.Location))
        {
            Assert.DoesNotContain(zip.Entries, entry => entry.FullName == "data/stable.txt");
            Assert.Contains(zip.Entries, entry => entry.FullName == "data/nested/данные.txt");
            Assert.Contains(zip.Entries, entry => entry.FullName == "data/new.txt");
        }
        Assert.Equal(2, (await catalog.ReadAsync()).Count); // N=1 must retain the full basis.
        Assert.Contains((await history.ReadAsync()).Last().Warnings!, warning => warning.Contains("вся инкрементная цепочка"));
        var beforeCatalog = JsonSerializer.Serialize(await catalog.ReadAsync()); var beforeRuns = JsonSerializer.Serialize(await history.ReadAsync());
        var unrelated = Path.Combine(RestoreParent, "customer.txt"); await File.WriteAllTextAsync(unrelated, "keep");
        var restored = await Restorer().RestoreAsync(increment, Job, RestoreParent);
        Assert.Equal(2, restored.Archives); Assert.Equal(3, restored.Files);
        Assert.Equal("unchanged", await File.ReadAllTextAsync(Path.Combine(restored.Directory, "stable.txt")));
        Assert.Equal("after", await File.ReadAllTextAsync(Path.Combine(restored.Directory, "nested", "данные.txt")));
        Assert.Equal("added", await File.ReadAllTextAsync(Path.Combine(restored.Directory, "new.txt")));
        Assert.False(File.Exists(Path.Combine(restored.Directory, "remove.txt"))); Assert.True(Directory.Exists(Path.Combine(restored.Directory, "empty")));
        Assert.Equal("keep", await File.ReadAllTextAsync(unrelated));
        Assert.Equal(beforeCatalog, JsonSerializer.Serialize(await catalog.ReadAsync())); Assert.Equal(beforeRuns, JsonSerializer.Serialize(await history.ReadAsync()));
    }

    [Fact]
    public async Task SameLengthAndTimestampContentChangeIsIncludedAndNoChangeIncrementStillRestores()
    {
        var file = Path.Combine(Source, "a.txt"); await File.WriteAllTextAsync(file, "1111"); var time = File.GetLastWriteTimeUtc(file);
        await Run(); await File.WriteAllTextAsync(file, "2222"); File.SetLastWriteTimeUtc(file, time);
        var changed = await Run();
        using (var zip = ZipFile.OpenRead(changed.Backup.Location)) Assert.Contains(zip.Entries, entry => entry.FullName == "data/a.txt");
        var noChangesJob = Job with { FullBackupEvery = 7 };
        var noChanges = await Run(noChangesJob);
        using (var zip = ZipFile.OpenRead(noChanges.Backup.Location)) Assert.Single(zip.Entries);
        var restored = await Restorer().RestoreAsync(noChanges, noChangesJob, RestoreParent);
        Assert.Equal(3, restored.Archives); Assert.Equal("2222", await File.ReadAllTextAsync(Path.Combine(restored.Directory, "a.txt")));
    }

    [Fact]
    public async Task PeriodicFullReleasesOldChainAfterVerifiedRegistration()
    {
        var file = Path.Combine(Source, "a.txt"); await File.WriteAllTextAsync(file, "1");
        await Run(); await File.WriteAllTextAsync(file, "2"); await Run();
        await File.WriteAllTextAsync(file, "3"); var third = await Run(); Assert.Equal(2, third.Backup.FileSnapshot!.Depth);
        var old = (await catalog.ReadAsync()).ToArray(); Assert.Equal(3, old.Length);
        await File.WriteAllTextAsync(file, "4"); var next = await Run(); Assert.True(next.Backup.FileSnapshot!.IsFull);
        Assert.Single(await catalog.ReadAsync()); Assert.All(old, record => Assert.False(File.Exists(record.Backup.Location)));
        Assert.Single(Directory.GetFiles(Job.DestinationDirectory, "*.zip"));
        Assert.Equal("4", await File.ReadAllTextAsync(Path.Combine((await Restorer().RestoreAsync(next, Job, RestoreParent)).Directory, "a.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SingleFileAndEmptyDirectoryProduceRestorableFullArchives(bool singleFile)
    {
        var job = Job with { FileBackupMode = FileBackupMode.Full };
        if (singleFile) { var file = Path.Combine(Source, "file.bin"); await File.WriteAllBytesAsync(file, [0, 1, 255]); job = job with { Source = file }; }
        var record = await Run(job); var restored = await Restorer().RestoreAsync(record, job, RestoreParent);
        Assert.Equal(singleFile ? 1 : 0, restored.Files); Assert.True(record.Backup.FileSnapshot!.IsFull);
        if (singleFile) Assert.Equal(new byte[] { 0, 1, 255 }, await File.ReadAllBytesAsync(Path.Combine(restored.Directory, "file.bin")));
        else Assert.Empty(Directory.GetFileSystemEntries(restored.Directory));
    }

    [Fact]
    public async Task EncryptedFullAndIncrementalRestoreWithStoredOrEnteredPassword()
    {
        var file = Path.Combine(Source, "secret.txt"); await File.WriteAllTextAsync(file, "first");
        var passwords = new Passwords(); var job = Job with { ArchivePasswordKey = "fixture-key" };
        await Run(job, passwords); await File.WriteAllTextAsync(file, "second"); var latest = await Run(job, passwords);
        using (var zip = new ICSharpCode.SharpZipLib.Zip.ZipFile(latest.Backup.Location))
            foreach (ICSharpCode.SharpZipLib.Zip.ZipEntry entry in zip) { Assert.True(entry.IsCrypted); Assert.Equal(256, entry.AESKeySize); }
        var restored = await Restorer(passwords).RestoreAsync(latest, job, RestoreParent);
        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(restored.Directory, "secret.txt")));
        var entered = await Restorer().RestoreAsync(latest, job, RestoreParent, passwordOverride: "fixture архив AES password");
        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(entered.Directory, "secret.txt")));
        await Assert.ThrowsAnyAsync<Exception>(() => Restorer().RestoreAsync(latest, job, RestoreParent, passwordOverride: "wrong"));
        Assert.Equal(2, Directory.GetDirectories(RestoreParent).Length);
        Assert.DoesNotContain(Directory.GetFileSystemEntries(RestoreParent), path => path.EndsWith(".partial"));
    }

    [Fact]
    public async Task NewDestinationAndPasswordChangeForceAFullBasisForEveryStore()
    {
        await File.WriteAllTextAsync(Path.Combine(Source, "a.txt"), "content"); await Run();
        var job = Job with { AdditionalDestinations = [new() { DestinationDirectory = Path.Combine(root, "extra"), KeepLast = 2 }] };
        var run = await Runner().RunAsync(job); Assert.True(run.Status == BackupRunStatus.Succeeded, run.Error);
        Assert.All(run.Destinations!, result => Assert.True(result.Record!.Backup.FileSnapshot!.IsFull));
        var protectedJob = job with { ArchivePasswordKey = "new-password-key" };
        var protectedRun = await Runner(new Passwords()).RunAsync(protectedJob); Assert.True(protectedRun.Status == BackupRunStatus.Succeeded, protectedRun.Error);
        Assert.All(protectedRun.Destinations!, result => Assert.True(result.Record!.Backup.FileSnapshot!.IsFull));
    }

    [Fact]
    public async Task CorruptIndexForcesFullAndMissingArchiveRefusesRestoreWithoutPublishing()
    {
        await File.WriteAllTextAsync(Path.Combine(Source, "a.txt"), "content"); var first = await Run();
        var state = Path.Combine(root, "file-snapshots", Job.Id.ToString("N") + ".json"); await File.WriteAllTextAsync(state, "broken JSON");
        var full = await Run(); Assert.True(full.Backup.FileSnapshot!.IsFull);
        var inc = await Run(); Assert.False(inc.Backup.FileSnapshot!.IsFull);
        File.Delete(full.Backup.Location);
        await Assert.ThrowsAnyAsync<Exception>(() => Restorer().RestoreAsync(inc, Job, RestoreParent));
        Assert.Empty(Directory.GetFileSystemEntries(RestoreParent));
    }

    [Fact]
    public async Task FailedSourceCopyKeepsPreviousChainAndCache()
    {
        var file = Path.Combine(Source, "a.txt"); await File.WriteAllTextAsync(file, "content"); var first = await Run();
        var before = JsonSerializer.Serialize(await catalog.ReadAsync()); var state = Path.Combine(root, "file-snapshots", Job.Id.ToString("N") + ".json");
        var bytes = await File.ReadAllBytesAsync(state);
        await using (var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        { var failed = await Runner().RunAsync(Job); Assert.Equal(BackupRunStatus.Failed, failed.Status); }
        Assert.Equal(before, JsonSerializer.Serialize(await catalog.ReadAsync())); Assert.Equal(bytes, await File.ReadAllBytesAsync(state));
        Assert.True(File.Exists(first.Backup.Location));
    }

    [Fact]
    public async Task StorageOrTemporaryFolderInsideSourceIsRejectedWithoutRecursiveBackup()
    {
        await File.WriteAllTextAsync(Path.Combine(Source, "a.txt"), "content");
        foreach (var job in new[] { Job with { DestinationDirectory = Path.Combine(Source, "copies") }, Job with { TemporaryDirectory = Path.Combine(Source, "work") } })
        { var failed = await Runner().RunAsync(job); Assert.Equal(BackupRunStatus.Failed, failed.Status); Assert.Contains("вне исходной папки", failed.Error); }
        Assert.Empty(await catalog.ReadAsync()); Assert.False(Directory.Exists(Path.Combine(Source, "copies")));
    }
    [Fact]
    public async Task IndependentStoreLimitsRetainDifferentNumbersOfWholeChains()
    {
        await File.WriteAllTextAsync(Path.Combine(Source, "a.txt"), "content");
        var job = Job with { FullBackupEvery = 2, AdditionalDestinations = [new() { DestinationDirectory = Path.Combine(root, "extra"), KeepLast = 3 }] };
        for (var i = 0; i < 5; i++)
        { var run = await Runner().RunAsync(job); Assert.True(run.Status == BackupRunStatus.Succeeded, run.Error); }
        var records = await catalog.ReadAsync();
        var primary = records.Where(record => Path.GetDirectoryName(record.Backup.Location) == job.DestinationDirectory).ToArray();
        var secondary = records.Where(record => Path.GetDirectoryName(record.Backup.Location) != job.DestinationDirectory).ToArray();
        Assert.Single(primary); Assert.Equal(3, secondary.Length);
        foreach (var record in records) FileBackupChains.Resolve(record, records);
        var selected = secondary.OrderBy(record => record.CompletedUtc).First();
        Assert.Equal("content", await File.ReadAllTextAsync(Path.Combine((await Restorer().RestoreAsync(selected, job, RestoreParent)).Directory, "a.txt")));
    }

    private sealed class RefuseDelete(string refused) : IBackupStorage
    {
        private readonly FileBackupStorage inner = new();
        public Task<StoredBackup> StoreAsync(string path, BackupJob job, CancellationToken token = default) => inner.StoreAsync(path, job, token);
        public Task CopyToAsync(StoredBackup backup, BackupJob job, Stream output, CancellationToken token = default) => inner.CopyToAsync(backup, job, output, token);
        public Task VerifyAsync(StoredBackup backup, BackupJob job, CancellationToken token = default) => inner.VerifyAsync(backup, job, token);
        public Task DeleteAsync(StoredBackup backup, BackupJob job, CancellationToken token = default) => backup.Location == refused
            ? Task.FromException(new IOException("fixture delete denied")) : inner.DeleteAsync(backup, job, token);
    }

    private sealed class FailOneTarget(string target) : IBackupStorage
    {
        private readonly FileBackupStorage inner = new();
        public bool Failing { get; set; }
        public Task<StoredBackup> StoreAsync(string path, BackupJob job, CancellationToken token = default) => Failing && job.DestinationDirectory == target
            ? Task.FromException<StoredBackup>(new IOException("fixture target unavailable")) : inner.StoreAsync(path, job, token);
        public Task CopyToAsync(StoredBackup backup, BackupJob job, Stream output, CancellationToken token = default) => inner.CopyToAsync(backup, job, output, token);
        public Task VerifyAsync(StoredBackup backup, BackupJob job, CancellationToken token = default) => inner.VerifyAsync(backup, job, token);
        public Task DeleteAsync(StoredBackup backup, BackupJob job, CancellationToken token = default) => inner.DeleteAsync(backup, job, token);
    }

    [Fact]
    public async Task PartialIncrementDeliveryForcesFreshFullBasisWhenTargetReturns()
    {
        var extra = Path.Combine(root, "extra"); var file = Path.Combine(Source, "a.txt"); await File.WriteAllTextAsync(file, "before");
        var job = Job with { FullBackupEvery = 7, AdditionalDestinations = [new() { DestinationDirectory = extra, KeepLast = 2 }] };
        var routed = new FailOneTarget(extra);
        var runner = new LocalBackupRunner(new NoDatabase(), new BackupCompletion(routed, catalog), history, Path.Combine(root, "default-work"));
        Assert.Equal(BackupRunStatus.Succeeded, (await runner.RunAsync(job)).Status);
        var extraBasis = (await catalog.ReadAsync()).Single(record => Path.GetDirectoryName(record.Backup.Location) == extra);
        routed.Failing = true; await File.WriteAllTextAsync(file, "after"); var partial = await runner.RunAsync(job);
        Assert.Equal(BackupRunStatus.PartiallySucceeded, partial.Status);
        Assert.Single(partial.Destinations!, result => result.Record is not null);
        Assert.False(partial.Backup!.FileSnapshot!.IsFull); Assert.True(File.Exists(extraBasis.Backup.Location));
        Assert.True(Directory.Exists(partial.WorkDirectory));
        routed.Failing = false; var recovered = await runner.RunAsync(job); Assert.Equal(BackupRunStatus.Succeeded, recovered.Status);
        Assert.All(recovered.Destinations!, destination => Assert.True(destination.Record!.Backup.FileSnapshot!.IsFull));
        foreach (var destination in recovered.Destinations!)
        {
            var restored = await Restorer().RestoreAsync(destination.Record!, job, RestoreParent);
            Assert.Equal(1, restored.Archives); Assert.Equal("after", await File.ReadAllTextAsync(Path.Combine(restored.Directory, "a.txt")));
        }
    }

    [Fact]
    public async Task FailedDeletionOfIncrementKeepsItsFullBasis()
    {
        await File.WriteAllTextAsync(Path.Combine(Source, "a.txt"), "content");
        var job = Job with { FullBackupEvery = 2, KeepLast = 10 };
        var full = await Run(job); var increment = await Run(job); var newest = await Run(job);
        var result = await new BackupRetention(catalog, new RefuseDelete(increment.Backup.Location)).ApplyAsync(job with { KeepLast = 1 }, newest);
        Assert.Single(result.Failures); Assert.Empty(result.Deleted);
        Assert.True(File.Exists(full.Backup.Location)); Assert.Equal(3, (await catalog.ReadAsync()).Count);
        Assert.Equal(2, (await Restorer().RestoreAsync(increment, job, RestoreParent)).Archives);
    }

    [Fact]
    public async Task MissingCatalogAncestorPreventsRestoreAndRetentionBeforeDeletion()
    {
        await File.WriteAllTextAsync(Path.Combine(Source, "a.txt"), "content");
        var full = await Run(); var increment = await Run(); await catalog.RemoveAsync(full.Id);
        await Assert.ThrowsAsync<InvalidDataException>(() => Restorer().RestoreAsync(increment, Job, RestoreParent));
        await Assert.ThrowsAsync<InvalidDataException>(() => new BackupRetention(catalog, storage).ApplyAsync(Job, increment));
        Assert.True(File.Exists(full.Backup.Location)); Assert.True(File.Exists(increment.Backup.Location));
        Assert.Empty(Directory.GetFileSystemEntries(RestoreParent));
        var replacement = await Run(); Assert.True(replacement.Backup.FileSnapshot!.IsFull);
    }

    [Fact]
    public async Task CancelledRestoreRemovesOnlyItsOwnStagingAndLeavesCatalogUnchanged()
    {
        await File.WriteAllTextAsync(Path.Combine(Source, "a.txt"), "content"); var record = await Run();
        var before = JsonSerializer.Serialize(await catalog.ReadAsync());
        var marker = Path.Combine(RestoreParent, "customer.txt"); await File.WriteAllTextAsync(marker, "keep");
        using var cancellation = new CancellationTokenSource();
        var restorer = new FileBackupRestorer(new CancelCopy(cancellation), catalog, Path.Combine(root, "default-work"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restorer.RestoreAsync(record, Job, RestoreParent, cancellation.Token));
        Assert.Equal(new[] { marker }, Directory.GetFileSystemEntries(RestoreParent));
        Assert.Equal(before, JsonSerializer.Serialize(await catalog.ReadAsync()));
        Assert.Empty(Directory.GetDirectories(Job.TemporaryDirectory!, "file-restore-*"));
    }

    private sealed class CancelCopy(CancellationTokenSource cancellation) : IBackupStorage
    {
        private readonly FileBackupStorage inner = new();
        public Task<StoredBackup> StoreAsync(string path, BackupJob job, CancellationToken token = default) => inner.StoreAsync(path, job, token);
        public async Task CopyToAsync(StoredBackup backup, BackupJob job, Stream output, CancellationToken token = default)
        { await inner.CopyToAsync(backup, job, output, token); cancellation.Cancel(); }
        public Task VerifyAsync(StoredBackup backup, BackupJob job, CancellationToken token = default) => inner.VerifyAsync(backup, job, token);
        public Task DeleteAsync(StoredBackup backup, BackupJob job, CancellationToken token = default) => inner.DeleteAsync(backup, job, token);
    }

    [Theory]
    [InlineData("../customer.txt")]
    [InlineData("NUL.txt")]
    [InlineData("COM¹.txt")]
    [InlineData("C:/customer.txt")]
    public async Task UnsafeManifestPathCannotWriteOutsideOwnedRestoreDirectory(string unsafePath)
    {
        await File.WriteAllTextAsync(Path.Combine(Source, "a.txt"), "content"); var original = await Run();
        var marker = Path.Combine(RestoreParent, "customer.txt"); await File.WriteAllTextAsync(marker, "keep");
        using (var zip = ZipFile.Open(original.Backup.Location, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry(FileBackupManifest.ArchiveEntryName)!;
            System.Text.Json.Nodes.JsonNode manifest;
            using (var input = entry.Open()) manifest = System.Text.Json.Nodes.JsonNode.Parse(input)!;
            manifest["Files"]![0]!["Path"] = unsafePath; manifest["ChangedFiles"]![0] = unsafePath;
            entry.Delete();
            using var output = new StreamWriter(zip.CreateEntry(FileBackupManifest.ArchiveEntryName).Open()); output.Write(manifest.ToJsonString());
        }
        await catalog.RemoveAsync(original.Id);
        var bytes = await File.ReadAllBytesAsync(original.Backup.Location);
        var rewritten = await catalog.RegisterAsync(Job, original.Backup with { Length = bytes.Length,
            Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) }, DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<InvalidDataException>(() => Restorer().RestoreAsync(rewritten, Job, RestoreParent));
        Assert.Equal("keep", await File.ReadAllTextAsync(marker)); Assert.Equal(new[] { marker }, Directory.GetFileSystemEntries(RestoreParent));
    }

    private sealed class RestoreProgress(Action<FileRestoreProgress> observe) : IProgress<FileRestoreProgress>
    { public void Report(FileRestoreProgress value) => observe(value); }

    [Fact]
    public async Task PointAndLatestAuditDetectMissingOrCorruptFullBasisEvenWhenIncrementIsIntact()
    {
        var file = Path.Combine(Source, "a.txt"); await File.WriteAllTextAsync(file, "before"); var full = await Run();
        await File.WriteAllTextAsync(file, "after"); var increment = await Run();
        var verifier = new BackupPointVerifier(storage, catalog);
        Assert.Equal(2, await verifier.VerifyAsync(increment, Job));
        var beforeCatalog = JsonSerializer.Serialize(await catalog.ReadAsync()); var beforeRuns = JsonSerializer.Serialize(await history.ReadAsync());
        var original = await File.ReadAllBytesAsync(full.Backup.Location);
        await File.WriteAllTextAsync(full.Backup.Location, "corrupt full basis");
        await new BackupCopyVerifier(storage).VerifyAsync(increment, Job); // The final ZIP alone is still intact.
        var corrupt = await Assert.ThrowsAsync<IOException>(() => verifier.VerifyAsync(increment, Job));
        Assert.Contains(full.Backup.Location, corrupt.Message);
        Assert.Equal(BackupAuditStatus.Failed, (await new LatestBackupAudit(storage, catalog).CheckAsync(Job)).Destinations.Single().Status);
        File.Delete(full.Backup.Location);
        await Assert.ThrowsAsync<IOException>(() => verifier.VerifyAsync(increment, Job));
        Assert.Equal(BackupAuditStatus.Failed, (await new LatestBackupAudit(storage, catalog).CheckAsync(Job)).Destinations.Single().Status);
        await File.WriteAllBytesAsync(full.Backup.Location, original);
        Assert.Equal(BackupAuditStatus.Verified, (await new LatestBackupAudit(storage, catalog).CheckAsync(Job)).Destinations.Single().Status);
        Assert.Equal(beforeCatalog, JsonSerializer.Serialize(await catalog.ReadAsync())); Assert.Equal(beforeRuns, JsonSerializer.Serialize(await history.ReadAsync()));
    }

    [Fact]
    public async Task RestoreReportsArchivesAndFinalVerificationAndCanCancelBeforePublishing()
    {
        var file = Path.Combine(Source, "a.txt"); await File.WriteAllTextAsync(file, "before"); await Run();
        await File.WriteAllTextAsync(file, "after"); var increment = await Run();
        var observed = new List<FileRestoreProgress>();
        var result = await Restorer().RestoreAsync(increment, Job, RestoreParent, progress: new RestoreProgress(observed.Add));
        Assert.Equal(2, result.Archives);
        Assert.Contains(observed, value => value.Phase == FileRestorePhase.TransferringArchive && value.ArchiveNumber == 1);
        Assert.Contains(observed, value => value.Phase == FileRestorePhase.VerifyingArchive && value.ArchiveNumber == 2 && value.Completed == value.Total);
        Assert.Contains(observed, value => value.Phase == FileRestorePhase.ApplyingSnapshot && value.ArchiveNumber == 2 && value.Completed == 1);
        Assert.Contains(observed, value => value.Phase == FileRestorePhase.VerifyingFiles && value.Completed == value.Total);
        Assert.Equal(FileRestorePhase.Completed, observed.Last().Phase);
        Assert.All(observed, value => { Assert.InRange(value.ArchiveNumber, 1, 2); Assert.Equal(2, value.Archives); Assert.InRange(value.Completed, 0, value.Total); });
        using var cancellation = new CancellationTokenSource(); var cancelled = new List<FileRestoreProgress>();
        var before = JsonSerializer.Serialize(await catalog.ReadAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Restorer().RestoreAsync(increment, Job, RestoreParent, cancellation.Token,
            progress: new RestoreProgress(value => { cancelled.Add(value); if (value.Phase == FileRestorePhase.Publishing) cancellation.Cancel(); })));
        Assert.DoesNotContain(cancelled, value => value.Phase == FileRestorePhase.Completed);
        Assert.Equal(new[] { result.Directory }, Directory.GetDirectories(RestoreParent));
        Assert.Empty(Directory.GetDirectories(Job.TemporaryDirectory!, "file-restore-*"));
        Assert.Equal(before, JsonSerializer.Serialize(await catalog.ReadAsync()));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
