using System.IO.Compression;
using System.Text.Json;
using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class AccessibleFilesBackupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "accessible-backup-" + Guid.NewGuid());
    private readonly BackupCatalog catalog;
    private readonly BackupRunHistory history;
    private readonly BackupJob job;
    private readonly FileBackupStorage storage = new();
    private sealed class Denied : IBackupExecutor
    { public Task<string> CreateAsync(BackupJob job, string work, CancellationToken token) => throw new UnauthorizedAccessException("fixture source denied"); }
    private sealed class Fatal : IBackupExecutor
    { public Task<string> CreateAsync(BackupJob job, string work, CancellationToken token) => throw new IOException("fixture device error"); }
    public AccessibleFilesBackupTests()
    {
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        job = new() { Name = "Backup SERV-DOC", Kind = DatabaseKind.FilesAndFolders, Source = source,
            DestinationDirectory = Path.Combine(root, "copies"), FileBackupMode = FileBackupMode.Incremental, KeepLast = 2 };
        catalog = new(Path.Combine(root, "catalog.json")); history = new(Path.Combine(root, "history.json"));
    }
    public void Dispose() => Directory.Delete(root, true);
    private static FileBackupManifest ReadManifest(string path)
    {
        using var zip = ZipFile.OpenRead(path); using var stream = zip.GetEntry("onec-backup-manifest.json")!.Open();
        var manifest = JsonSerializer.Deserialize<FileBackupManifest>(stream)!; manifest.Validate(); return manifest;
    }
    private LocalBackupRunner Runner(bool denied = false, Func<string, IEnumerable<string>>? enumerate = null, Func<string, Stream>? open = null)
        => new(new Denied(), new(storage, catalog), history, Path.Combine(root, "work"),
            fileAccessWrapper: denied ? _ => new AccessibleFilesBackupExecutor(new Denied(), enumerateSource: enumerate, openSource: open) : null);
    [Fact]
    public async Task UnreadableDirectoryCreatesVerifiedPartialArchiveWithWarningsAndRestoresAvailableFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(job.Source, "available.txt"), "available");
        var blocked = Path.Combine(job.Source, "Юрист"); Directory.CreateDirectory(blocked);
        await File.WriteAllTextAsync(Path.Combine(blocked, "private.txt"), "must not appear");
        IEnumerable<string> Enumerate(string path) => path == blocked ? throw new UnauthorizedAccessException() : Directory.EnumerateFileSystemEntries(path);
        var result = await Runner(true, Enumerate).RunAsync(job);
        Assert.Equal(BackupRunStatus.Succeeded, result.Status); Assert.Equal("С предупреждениями", result.StatusDescription);
        Assert.Contains(result.Warnings!, text => text.Contains("Юрист")); Assert.True(result.Backup!.FileSnapshot!.IsIncomplete);
        var manifest = ReadManifest(result.Backup.Location);
        Assert.Equal(2, manifest.FormatVersion); Assert.Equal(new[] { "Юрист" }, manifest.SkippedPaths);
        Assert.Null(manifest.ParentSnapshotId); Assert.Empty(manifest.DeletedFiles);
        using (var zip = ZipFile.OpenRead(result.Backup.Location))
        { Assert.NotNull(zip.GetEntry("data/available.txt")); Assert.Null(zip.GetEntry("data/Юрист/private.txt")); }
        var parent = Path.Combine(root, "restore"); Directory.CreateDirectory(parent);
        var record = Assert.Single(await catalog.ReadAsync());
        var restored = await new FileBackupRestorer(storage, catalog, Path.Combine(root, "restore-work")).RestoreAsync(record, job, parent);
        Assert.Equal("available", await File.ReadAllTextAsync(Path.Combine(restored.Directory, "available.txt")));
        Assert.Equal(manifest.SkippedPaths, restored.SkippedPaths);
    }
    [Fact]
    public async Task LockedFileIsSkippedBeforeZipEntryAndSingleUnavailableFileHasExplicitEmptyWarning()
    {
        var file = Path.Combine(job.Source, "locked.txt"); await File.WriteAllTextAsync(file, "locked");
        var single = job with { Source = file };
        var result = await Runner(true, open: _ => throw new IOException("locked", unchecked((int)0x80070020))).RunAsync(single);
        Assert.Equal(BackupRunStatus.Succeeded, result.Status); Assert.Contains(result.Warnings!, warning => warning.Contains("locked.txt"));
        var manifest = ReadManifest(result.Backup!.Location);
        Assert.Empty(manifest.Files); Assert.True(manifest.GetSnapshot().IsIncomplete); Assert.Equal(new[] { "locked.txt" }, manifest.SkippedPaths);
    }
    [Fact]
    public async Task PartialDoesNotDeleteGoodCopiesOrConsumeGoodQuotaAndNextIncrementalStartsFull()
    {
        var file = Path.Combine(job.Source, "available.txt"); await File.WriteAllTextAsync(file, "v1");
        var first = await Runner().RunAsync(job);
        Assert.Equal(BackupRunStatus.Succeeded, first.Status);
        await File.WriteAllTextAsync(file, "v2"); var second = await Runner().RunAsync(job);
        Assert.False(second.Backup!.FileSnapshot!.IsFull);
        var partial = await Runner(true, open: _ => throw new UnauthorizedAccessException()).RunAsync(job);
        Assert.True(partial.Backup!.FileSnapshot!.IsIncomplete);
        Assert.True(File.Exists(first.Backup!.Location)); Assert.True(File.Exists(second.Backup.Location));
        Assert.Equal(3, (await catalog.ReadAsync()).Count);
        var recovered = await Runner().RunAsync(job);
        Assert.True(recovered.Backup!.FileSnapshot!.IsFull); Assert.False(recovered.Backup.FileSnapshot.IsIncomplete);
        Assert.True(File.Exists(partial.Backup.Location)); Assert.True(File.Exists(second.Backup.Location));
        await File.WriteAllTextAsync(file, "v3"); var incremental = await Runner().RunAsync(job);
        Assert.False(incremental.Backup!.FileSnapshot!.IsFull);
    }
    [Fact]
    public async Task DeviceFailureAndCancellationRemainErrorsAndNeverPublishAnArchive()
    {
        var work = Path.Combine(root, "fatal-work");
        var executor = new AccessibleFilesBackupExecutor(new Fatal());
        await Assert.ThrowsAsync<IOException>(() => executor.CreateAsync(job, work, CancellationToken.None));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var access = new AccessibleFilesBackupExecutor(new Denied());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => access.CreateAsync(job, work, cancellation.Token));
        Assert.Empty(await catalog.ReadAsync()); Assert.False(Directory.Exists(work));
    }
    private sealed class FailedRead(byte[] data) : MemoryStream(data)
    {
        private bool read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (read) throw new IOException("fixture source read failure");
            read = true;
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 2)], token);
        }
    }
    [Theory]
    [InlineData("size")]
    [InlineData("read")]
    [InlineData("deleted")]
    public async Task SourceFileFailureKeepsOtherFilesAndDeliversVerifiedPartialToEveryDestination(string failure)
    {
        var bad = Path.Combine(job.Source, "a-changing.xls");
        await File.WriteAllTextAsync(bad, "changing spreadsheet");
        await File.WriteAllTextAsync(Path.Combine(job.Source, "b-available.txt"), "available");
        Stream Open(string path)
        {
            if (path != bad) return File.OpenRead(path);
            var bytes = File.ReadAllBytes(path);
            if (failure == "read") return new FailedRead(bytes);
            if (failure == "deleted") { File.Delete(path); throw new FileNotFoundException("fixture deleted source", path); }
            File.AppendAllText(path, "changed after scan");
            return File.OpenRead(path);
        }
        var targets = job with { AdditionalDestinations = [new() { StorageKind = StorageKind.LocalDirectory,
            DestinationDirectory = Path.Combine(root, "other-copies"), KeepLast = 3 }] };
        var result = await Runner(true, open: Open).RunAsync(targets);
        Assert.Equal(BackupRunStatus.Succeeded, result.Status);
        Assert.Contains(result.Warnings!, warning => warning.Contains("a-changing.xls"));
        var records = await catalog.ReadAsync(); Assert.Equal(2, records.Count);
        foreach (var record in records)
        {
            var manifest = ReadManifest(record.Backup.Location);
            Assert.True(manifest.GetSnapshot().IsIncomplete);
            Assert.Equal(new[] { "a-changing.xls" }, manifest.SkippedPaths);
            Assert.Equal("b-available.txt", Assert.Single(manifest.Files).Path);
            using var zip = ZipFile.OpenRead(record.Backup.Location);
            Assert.Null(zip.GetEntry("data/a-changing.xls"));
            using var reader = new StreamReader(zip.GetEntry("data/b-available.txt")!.Open());
            Assert.Equal("available", await reader.ReadToEndAsync());
        }
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "work"), "*.tmp", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task NormalSizeChangeAfterScanRetriesAndCreatesCopyInsteadOfFailingTheJob()
    {
        var changing = Path.Combine(job.Source, "changing.xls"); await File.WriteAllTextAsync(changing, "before");
        await File.WriteAllTextAsync(Path.Combine(job.Source, "available.txt"), "available");
        var changed = false;
        var runner = new LocalBackupRunner(new Denied(), new(storage, catalog), history, Path.Combine(root, "work"), progress: value =>
        {
            if (value.Progress.Stage == BackupStage.ArchivingFiles && !changed)
            { changed = true; File.AppendAllText(changing, " after scan"); }
        });
        var result = await runner.RunAsync(job);
        Assert.True(changed); Assert.Equal(BackupRunStatus.Succeeded, result.Status);
        var manifest = ReadManifest(result.Backup!.Location); Assert.Equal(2, manifest.Files.Count);
        using var zip = ZipFile.OpenRead(result.Backup.Location);
        using var reader = new StreamReader(zip.GetEntry("data/changing.xls")!.Open());
        Assert.Equal("before after scan", await reader.ReadToEndAsync());
    }
    private sealed class FailedSecondPass(byte[] data) : MemoryStream(data)
    {
        private bool copying;
        private bool copied;
        public override long Position { get => base.Position; set { base.Position = value; copying = true; } }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (copying && copied) throw new IOException("fixture read failure inside ZIP entry");
            if (copying) { copied = true; buffer = buffer[..Math.Min(buffer.Length, 2)]; }
            return base.ReadAsync(buffer, token);
        }
    }
    [Fact]
    public async Task ReadFailureInsideNormalZipEntryRetriesWithoutPublishingBrokenZip()
    {
        await File.WriteAllTextAsync(Path.Combine(job.Source, "data.txt"), "complete data");
        var normal = new FileFolderBackupExecutor(catalog, new(Path.Combine(root, "snapshots")),
            openSource: path => new FailedSecondPass(File.ReadAllBytes(path)));
        var runner = new LocalBackupRunner(new Denied(), new(storage, catalog), history, Path.Combine(root, "work"),
            fileAccessWrapper: _ => new AccessibleFilesBackupExecutor(normal));
        var result = await runner.RunAsync(job);
        Assert.Equal(BackupRunStatus.Succeeded, result.Status);
        var manifest = ReadManifest(result.Backup!.Location); Assert.Single(manifest.Files);
        using var zip = ZipFile.OpenRead(result.Backup.Location);
        using var reader = new StreamReader(zip.GetEntry("data/data.txt")!.Open());
        Assert.Equal("complete data", await reader.ReadToEndAsync());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "work"), "*.partial", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task LaterSourceEditsDoNotInvalidateAlreadyCapturedFileVersions()
    {
        var first = Path.Combine(job.Source, "a.txt"); await File.WriteAllTextAsync(first, "captured");
        await File.WriteAllTextAsync(Path.Combine(job.Source, "b.txt"), "later");
        Stream Open(string path)
        {
            if (Path.GetFileName(path) == "b.txt") File.WriteAllText(first, "edited later");
            return File.OpenRead(path);
        }
        var result = await Runner(true, open: Open).RunAsync(job);
        Assert.Equal(BackupRunStatus.Succeeded, result.Status);
        using var zip = ZipFile.OpenRead(result.Backup!.Location);
        using var reader = new StreamReader(zip.GetEntry("data/a.txt")!.Open());
        Assert.Equal("captured", await reader.ReadToEndAsync());
    }
    private sealed class Passwords : IArchivePasswordStore
    { public Task<string> GetAsync(string key, CancellationToken token = default) => Task.FromResult("fixture-password"); }
    [Fact]
    public async Task PartialEncryptedCopyCanBeRestoredAndTemporaryPlaintextIsRemoved()
    {
        await File.WriteAllTextAsync(Path.Combine(job.Source, "available.txt"), "private available data");
        await File.WriteAllTextAsync(Path.Combine(job.Source, "bad.xls"), "bad");
        var passwords = new Passwords();
        Stream Open(string path) => Path.GetFileName(path) == "bad.xls" ? new FailedRead(File.ReadAllBytes(path)) : File.OpenRead(path);
        var encrypted = job with { ArchiveCopies = true, ArchivePasswordKey = Guid.NewGuid().ToString("N") };
        var runner = new LocalBackupRunner(new Denied(), new(storage, catalog, passwords), history, Path.Combine(root, "work"),
            fileAccessWrapper: _ => new AccessibleFilesBackupExecutor(new Denied(), passwords, openSource: Open));
        var result = await runner.RunAsync(encrypted);
        Assert.Equal(BackupRunStatus.Succeeded, result.Status); Assert.True(result.Backup!.FileSnapshot!.IsIncomplete);
        var restore = Path.Combine(root, "restore"); Directory.CreateDirectory(restore);
        var restored = await new FileBackupRestorer(storage, catalog, Path.Combine(root, "restore-work"), passwords)
            .RestoreAsync(Assert.Single(await catalog.ReadAsync()), encrypted, restore);
        Assert.Equal("private available data", await File.ReadAllTextAsync(Path.Combine(restored.Directory, "available.txt")));
        Assert.Equal(new[] { "bad.xls" }, restored.SkippedPaths);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(root, "work"), "source-stage-*", SearchOption.AllDirectories));
    }
    private sealed class ChangedSecondPass(byte[] bytes) : MemoryStream(bytes.ToArray())
    {
        public override long Position
        {
            get => base.Position;
            set
            {
                base.Position = value;
                if (value == 0) { var changed = bytes.ToArray(); changed[0] ^= 1; base.Write(changed); base.Position = 0; }
            }
        }
    }
    [Fact]
    public async Task SameLengthContentChangeIsDetectedBeforePublishingAndRetriedSafely()
    {
        await File.WriteAllTextAsync(Path.Combine(job.Source, "spreadsheet.xls"), "original data");
        var normal = new FileFolderBackupExecutor(catalog, new(Path.Combine(root, "snapshots")),
            openSource: path => new ChangedSecondPass(File.ReadAllBytes(path)));
        var runner = new LocalBackupRunner(new Denied(), new(storage, catalog), history, Path.Combine(root, "work"),
            fileAccessWrapper: _ => new AccessibleFilesBackupExecutor(normal));
        var result = await runner.RunAsync(job);
        Assert.Equal(BackupRunStatus.Succeeded, result.Status);
        using var zip = ZipFile.OpenRead(result.Backup!.Location);
        using var reader = new StreamReader(zip.GetEntry("data/spreadsheet.xls")!.Open());
        Assert.Equal("original data", await reader.ReadToEndAsync());
    }
    [Fact]
    public async Task CancellationWhileStagingDoesNotPublishOrLeavePlaintextFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(job.Source, "data.txt"), "data");
        using var cancellation = new CancellationTokenSource();
        Stream Open(string path) { cancellation.Cancel(); return File.OpenRead(path); }
        var result = await Runner(true, open: Open).RunAsync(job, cancellation.Token);
        Assert.Equal(BackupRunStatus.Cancelled, result.Status); Assert.Empty(await catalog.ReadAsync());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "work"), "*.tmp", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(root, "work"), "source-stage-*", SearchOption.AllDirectories));
    }
    [Fact]
    public void IncompleteManifestCannotClaimAnIncrementalParentOrMarkCopiedDataAsSkipped()
    {
        var id = Guid.NewGuid();
        var manifest = new FileBackupManifest { SnapshotId = id, FullSnapshotId = id,
            SourceIdentity = new string('a', 64), FormatVersion = 2, SkippedPaths = ["blocked"] };
        manifest.Validate();
        Assert.Throws<InvalidDataException>(() => (manifest with { ParentSnapshotId = Guid.NewGuid(), Depth = 1 }).Validate());
        Assert.Throws<InvalidDataException>(() => (manifest with { SkippedPaths = ["../outside"] }).Validate());
        Assert.Throws<InvalidDataException>(() => (manifest with { SkippedPaths = ["blocked", "BLOCKED"] }).Validate());
    }
}
