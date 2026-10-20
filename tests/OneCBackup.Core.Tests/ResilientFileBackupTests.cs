using System.IO.Compression;
using System.Text.Json;
using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class ResilientFileBackupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "resilient-files-" + Guid.NewGuid());
    private readonly BackupCatalog catalog;
    private readonly BackupRunHistory history;
    private readonly FileBackupStorage storage = new();
    private readonly BackupJob job;
    private sealed class Unused : IBackupExecutor
    { public Task<string> CreateAsync(BackupJob job, string work, CancellationToken token) => throw new InvalidOperationException(); }
    public ResilientFileBackupTests()
    {
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        job = new() { Name = "Resilient", Kind = DatabaseKind.FilesAndFolders, Source = source,
            DestinationDirectory = Path.Combine(root, "copies"), FileBackupMode = FileBackupMode.Incremental,
            FullBackupEvery = 3, KeepLast = 1 };
        catalog = new(Path.Combine(root, "catalog.json")); history = new(Path.Combine(root, "history.json"));
    }
    public void Dispose() => Directory.Delete(root, true);
    private Task Write(string name, string value)
    {
        var path = Path.Combine(job.Source, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return File.WriteAllTextAsync(path, value);
    }
    private LocalBackupRunner Runner(Func<string, Stream>? open = null, Func<string, IEnumerable<string>>? enumerate = null,
        IArchivePasswordStore? passwords = null)
        => new(new Unused(), new(storage, catalog, passwords), history, Path.Combine(root, "work"),
            fileAccessWrapper: normal => new FileFolderBackupExecutor(catalog, new(Path.Combine(root, "file-snapshots")),
                passwords, openSource: open, enumerateSource: enumerate));
    private static FileBackupManifest Manifest(BackupRun run)
    {
        Assert.True(run.Status == BackupRunStatus.Succeeded, run.Error);
        using var zip = ZipFile.OpenRead(run.Backup!.Location);
        using var input = zip.GetEntry(FileBackupManifest.ArchiveEntryName)!.Open();
        var manifest = JsonSerializer.Deserialize<FileBackupManifest>(input)!; manifest.Validate(); return manifest;
    }
    private async Task<RestoredFileBackup> Restore(BackupRun run)
    {
        var parent = Path.Combine(root, "restores"); Directory.CreateDirectory(parent);
        var record = (await catalog.ReadAsync()).Single(record => record.Backup.Location == run.Backup!.Location);
        return await new FileBackupRestorer(storage, catalog, Path.Combine(root, "restore-work"))
            .RestoreAsync(record, job, parent);
    }
    private sealed class ReadFailure(byte[] bytes) : MemoryStream(bytes)
    {
        private bool started;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (started) throw new IOException("source failed after some bytes");
            started = true; return base.ReadAsync(buffer[..Math.Min(buffer.Length, 2)], token);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedFileIsRetriedAfterOthersAndSuccessfulFilesAreNeverReopened(bool persistent)
    {
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" }) await Write(name, name);
        var calls = new List<string>();
        Stream Open(string path)
        {
            var name = Path.GetFileName(path); calls.Add(name);
            return name == "b.txt" && (persistent || calls.Count(item => item == name) == 1)
                ? new ReadFailure(File.ReadAllBytes(path)) : File.OpenRead(path);
        }
        var run = await Runner(Open).RunAsync(job);
        var manifest = Manifest(run);
        Assert.Equal(new[] { "a.txt", "b.txt", "c.txt", "b.txt" }, calls);
        Assert.Equal(persistent, run.Backup!.FileSnapshot!.IsIncomplete);
        Assert.Equal(persistent ? 2 : 3, manifest.Files.Count);
        Assert.Equal(persistent ? new[] { "b.txt" } : null, manifest.SkippedPaths);
        Assert.Equal("a.txt", await File.ReadAllTextAsync(Path.Combine((await Restore(run)).Directory, "a.txt")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "work"), "*", SearchOption.AllDirectories));
        Assert.Single(await history.ReadAsync());
    }
    [Fact]
    public async Task PartialIncrementsRetainOldVersionsAndScheduledFullIsNotMoved()
    {
        await Write("good.txt", "good v1"); await Write("locked.txt", "locked v1");
        await Write("blocked/nested.txt", "nested v1"); await Write("deleted.txt", "to delete");
        var full = await Runner().RunAsync(job); var basis = Manifest(full);
        await Write("good.txt", "good v2"); await Write("locked.txt", "locked v2");
        await Write("blocked/nested.txt", "nested v2"); await Write("new.txt", "new data");
        File.Delete(Path.Combine(job.Source, "deleted.txt"));
        var calls = new List<string>();
        Stream Open(string path)
        {
            var name = Path.GetFileName(path); calls.Add(name);
            if (name is "locked.txt" or "new.txt") throw new UnauthorizedAccessException();
            return File.OpenRead(path);
        }
        IEnumerable<string> Enumerate(string path) => Path.GetFileName(path) == "blocked"
            ? throw new IOException("unavailable directory") : Directory.EnumerateFileSystemEntries(path);
        var partial = await Runner(Open, Enumerate).RunAsync(job);
        var first = Manifest(partial);
        Assert.Equal(3, first.FormatVersion); Assert.Equal(basis.SnapshotId, first.ParentSnapshotId);
        Assert.Equal(1, first.Depth); Assert.Equal(new[] { "good.txt" }, first.ChangedFiles);
        Assert.Equal(new[] { "deleted.txt" }, first.DeletedFiles);
        Assert.Equal(new[] { "blocked", "locked.txt", "new.txt" }, first.SkippedPaths);
        Assert.Equal(basis.Files.Single(file => file.Path == "locked.txt"), first.Files.Single(file => file.Path == "locked.txt"));
        Assert.Single(calls, path => path == "good.txt");
        Assert.Equal(2, calls.Count(path => path == "locked.txt")); Assert.Equal(2, calls.Count(path => path == "new.txt"));
        using (var zip = ZipFile.OpenRead(partial.Backup!.Location))
            Assert.Equal(new[] { "data/good.txt", FileBackupManifest.ArchiveEntryName }, zip.Entries.Select(entry => entry.FullName));
        var restored = await Restore(partial);
        Assert.Equal(2, restored.Archives); Assert.Equal(first.SkippedPaths, restored.SkippedPaths);
        Assert.Equal("good v2", await File.ReadAllTextAsync(Path.Combine(restored.Directory, "good.txt")));
        Assert.Equal("locked v1", await File.ReadAllTextAsync(Path.Combine(restored.Directory, "locked.txt")));
        Assert.Equal("nested v1", await File.ReadAllTextAsync(Path.Combine(restored.Directory, "blocked/nested.txt")));
        Assert.False(File.Exists(Path.Combine(restored.Directory, "new.txt")));
        Assert.False(File.Exists(Path.Combine(restored.Directory, "deleted.txt")));
        var next = await Runner(Open, Enumerate).RunAsync(job); var second = Manifest(next);
        Assert.Equal(first.SnapshotId, second.ParentSnapshotId); Assert.Equal(2, second.Depth);
        Assert.Empty(second.ChangedFiles); Assert.True(next.Backup!.FileSnapshot!.IsIncomplete);
        Assert.Equal(3, (await Restore(next)).Archives);
        Assert.True(File.Exists(full.Backup!.Location)); Assert.True(File.Exists(partial.Backup.Location));
        var scheduledFull = await Runner(Open, Enumerate).RunAsync(job); var last = Manifest(scheduledFull);
        Assert.Null(last.ParentSnapshotId); Assert.Equal(0, last.Depth);
        Assert.True(last.GetSnapshot().IsIncomplete); Assert.Equal(new[] { "good.txt" }, last.ChangedFiles);
        // A scheduled incomplete full also stays on the plan: its next backup is an increment.
        var recovered = await Runner().RunAsync(job); var recoveredManifest = Manifest(recovered);
        Assert.Equal(last.SnapshotId, recoveredManifest.ParentSnapshotId); Assert.Equal(1, recoveredManifest.Depth);
        Assert.Null(recoveredManifest.SkippedPaths);
        Assert.Equal("locked v2", await File.ReadAllTextAsync(Path.Combine((await Restore(recovered)).Directory, "locked.txt")));
        Assert.True(File.Exists(full.Backup.Location)); Assert.True(File.Exists(partial.Backup.Location));
    }
    [Fact]
    public async Task ReadableRetryRemovesWarningAndIncludesOnlyMissingChangeInNextIncrement()
    {
        await Write("good.txt", "good"); await Write("busy.txt", "old");
        var full = await Runner().RunAsync(job);
        await Write("busy.txt", "new");
        var partial = await Runner(path => Path.GetFileName(path) == "busy.txt"
            ? throw new UnauthorizedAccessException() : File.OpenRead(path)).RunAsync(job);
        Assert.True(Manifest(partial).GetSnapshot().IsIncomplete);
        var recovered = await Runner().RunAsync(job); var manifest = Manifest(recovered);
        Assert.Equal(partial.Backup!.FileSnapshot!.SnapshotId, manifest.ParentSnapshotId);
        Assert.Null(manifest.SkippedPaths); Assert.Equal(new[] { "busy.txt" }, manifest.ChangedFiles);
        var restored = await Restore(recovered);
        Assert.Equal(3, restored.Archives);
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(restored.Directory, "busy.txt")));
        Assert.True(File.Exists(full.Backup!.Location));
    }
    [Fact]
    public async Task PartialDirectoryEnumerationKeepsAlreadyFoundFilesAndRetriesOnlyAfterTheirCapture()
    {
        await Write("available.txt", "captured"); await Write("unknown.txt", "not enumerated");
        var opened = new List<string>(); var enumerations = 0;
        IEnumerable<string> Enumerate(string path)
        {
            enumerations++;
            if (enumerations == 2) Assert.Equal(new[] { "available.txt" }, opened);
            yield return Path.Combine(path, "available.txt");
            throw new IOException("enumeration interrupted");
        }
        Stream Open(string path) { opened.Add(Path.GetFileName(path)); return File.OpenRead(path); }
        var run = await Runner(Open, Enumerate).RunAsync(job); var manifest = Manifest(run);
        Assert.Equal(2, enumerations); Assert.Single(opened);
        Assert.Equal(3, manifest.FormatVersion); Assert.Equal(new[] { "." }, manifest.SkippedPaths);
        Assert.Equal("available.txt", Assert.Single(manifest.Files).Path);
        Assert.Equal("captured", await File.ReadAllTextAsync(Path.Combine((await Restore(run)).Directory, "available.txt")));
    }
    [Fact]
    public async Task LaterSourceChangesNeverInvalidateAnAlreadyArchivedVersion()
    {
        await Write("a.txt", "captured"); await Write("b.txt", "later");
        var calls = new List<string>();
        Stream Open(string path)
        {
            calls.Add(Path.GetFileName(path));
            if (Path.GetFileName(path) == "b.txt") File.WriteAllText(Path.Combine(job.Source, "a.txt"), "later edit");
            return File.OpenRead(path);
        }
        var run = await Runner(Open).RunAsync(job); Manifest(run);
        Assert.Equal(new[] { "a.txt", "b.txt" }, calls);
        Assert.Equal("captured", await File.ReadAllTextAsync(Path.Combine((await Restore(run)).Directory, "a.txt")));
    }
    private sealed class Passwords : IArchivePasswordStore
    { public Task<string> GetAsync(string key, CancellationToken token = default) => Task.FromResult("partial-increment-fixture"); }
    [Fact]
    [Trait("Category", "LargeFileBackup")]
    public async Task LateSourceFailureKeepsLargeArchivedFileWithoutReadingItAgain()
    {
        const long length = 128L * 1024 * 1024;
        var large = Path.Combine(job.Source, "a-large.bin");
        await using (var file = new FileStream(large, FileMode.CreateNew, FileAccess.Write))
        { file.SetLength(length); file.Position = length - 1; file.WriteByte(42); }
        await Write("b-broken.txt", "broken"); await Write("c-good.txt", "good");
        var calls = new List<string>();
        Stream Open(string path)
        {
            calls.Add(Path.GetFileName(path));
            return Path.GetFileName(path) == "b-broken.txt" ? new ReadFailure(File.ReadAllBytes(path)) : File.OpenRead(path);
        }
        var run = await Runner(Open).RunAsync(job); var manifest = Manifest(run);
        Assert.Equal(new[] { "a-large.bin", "b-broken.txt", "c-good.txt", "b-broken.txt" }, calls);
        Assert.Equal(new[] { "b-broken.txt" }, manifest.SkippedPaths);
        var restored = await Restore(run);
        using var input = File.OpenRead(Path.Combine(restored.Directory, "a-large.bin"));
        Assert.Equal(length, input.Length); input.Position = length - 1; Assert.Equal(42, input.ReadByte());
        Assert.Equal("good", await File.ReadAllTextAsync(Path.Combine(restored.Directory, "c-good.txt")));
    }
    [Fact]
    public async Task EncryptedPartialIncrementRestoresOldAndNewVersionsWithoutPlaintextResidue()
    {
        var passwords = new Passwords();
        var protectedJob = job with { ArchiveCopies = true, ArchivePasswordKey = "fixture" };
        await Write("good.txt", "old good"); await Write("busy.txt", "old busy");
        var first = await Runner(passwords: passwords).RunAsync(protectedJob);
        Assert.Equal(BackupRunStatus.Succeeded, first.Status);
        await Write("good.txt", "new good"); await Write("busy.txt", "new busy");
        var partial = await Runner(path => Path.GetFileName(path) == "busy.txt"
            ? throw new UnauthorizedAccessException() : File.OpenRead(path), passwords: passwords).RunAsync(protectedJob);
        Assert.True(partial.Status == BackupRunStatus.Succeeded, partial.Error);
        Assert.True(partial.Backup!.FileSnapshot!.IsIncomplete); Assert.False(partial.Backup.FileSnapshot.IsFull);
        var parent = Path.Combine(root, "encrypted-restores"); Directory.CreateDirectory(parent);
        var record = (await catalog.ReadAsync()).Single(record => record.Backup.Location == partial.Backup.Location);
        var restored = await new FileBackupRestorer(storage, catalog, Path.Combine(root, "restore-work"), passwords)
            .RestoreAsync(record, protectedJob, parent);
        Assert.Equal(new[] { "busy.txt" }, restored.SkippedPaths);
        Assert.Equal("new good", await File.ReadAllTextAsync(Path.Combine(restored.Directory, "good.txt")));
        Assert.Equal("old busy", await File.ReadAllTextAsync(Path.Combine(restored.Directory, "busy.txt")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "work"), "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "restore-work"), "*", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task ReplacedDirectoryDoesNotRetainConflictingOldChildrenDuringPartialEnumeration()
    {
        await Write("replaced/old.txt", "old child");
        var first = await Runner().RunAsync(job); Manifest(first);
        Directory.Delete(Path.Combine(job.Source, "replaced"), true);
        await Write("replaced", "replacement file");
        IEnumerable<string> Enumerate(string path)
        {
            yield return Path.Combine(path, "replaced");
            throw new IOException("enumeration interrupted");
        }
        var run = await Runner(enumerate: Enumerate).RunAsync(job); var manifest = Manifest(run);
        Assert.Equal(new[] { "." }, manifest.SkippedPaths);
        Assert.Equal("replaced", Assert.Single(manifest.Files).Path);
        Assert.Empty(manifest.Directories);
        var restored = await Restore(run);
        Assert.Equal("replacement file", await File.ReadAllTextAsync(Path.Combine(restored.Directory, "replaced")));
    }
    [Fact]
    public void FormatThreeRejectsDeletionOrNewContentClaimedForAnExactlySkippedFile()
    {
        var id = Guid.NewGuid(); var full = Guid.NewGuid();
        var file = new FileBackupEntry("busy.txt", 0, new string('a', 64), DateTimeOffset.UnixEpoch);
        var manifest = new FileBackupManifest { FormatVersion = 3, SnapshotId = id, FullSnapshotId = full,
            ParentSnapshotId = Guid.NewGuid(), Depth = 1, SourceIdentity = new string('b', 64),
            Files = [file], SkippedPaths = ["busy.txt"] };
        manifest.Validate();
        Assert.Throws<InvalidDataException>(() => (manifest with { ChangedFiles = ["busy.txt"] }).Validate());
        Assert.Throws<InvalidDataException>(() => (manifest with { Files = [], DeletedFiles = ["busy.txt"] }).Validate());
        Assert.Throws<InvalidDataException>(() => (manifest with { SkippedPaths = ["../outside"] }).Validate());
    }
}
