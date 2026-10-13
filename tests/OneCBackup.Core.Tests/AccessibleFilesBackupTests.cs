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
