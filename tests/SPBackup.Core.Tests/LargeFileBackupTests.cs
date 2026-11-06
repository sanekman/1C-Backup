using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class LargeFileBackupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "large-backup-" + Guid.NewGuid());
    public LargeFileBackupTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private static FileBackupManifest Manifest(IReadOnlyList<FileBackupEntry> files, IReadOnlyList<string>? skipped = null)
    {
        var id = Guid.NewGuid();
        return new() { SnapshotId = id, FullSnapshotId = id, SourceIdentity = new string('a', 64), Files = files,
            ChangedFiles = files.Select(file => file.Path).ToArray(), FormatVersion = skipped is null ? 1 : 2, SkippedPaths = skipped };
    }
    [Fact]
    public void StreamingHashPreservesExistingManifestBytesAndSkippedAncestorsRemainRejected()
    {
        var file = new FileBackupEntry("folder/file.txt", 0, new string('a', 64), DateTimeOffset.UnixEpoch);
        var manifest = Manifest([file]) with { Directories = ["folder"] };
        manifest.Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true });
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), manifest.GetSnapshot().ManifestSha256);
        foreach (var skipped in new[] { ".", "folder", "FOLDER/FILE.TXT" })
            Assert.Throws<InvalidDataException>(() => (manifest with { FormatVersion = 2, SkippedPaths = [skipped] }).Validate());
    }
    [Fact]
    public async Task IndexLargerThan32MiBAndOver100000FilesAndSkipsRoundTripsWithSameSnapshot()
    {
        var files = Enumerable.Range(0, 100001).Select(index => new FileBackupEntry(
            index.ToString("D6") + new string('x', 200), 0, new string('a', 64), DateTimeOffset.UnixEpoch)).ToArray();
        var skipped = Enumerable.Range(0, 100001).Select(index => "denied-" + index).ToArray();
        var manifest = Manifest(files, skipped); manifest.Validate();
        var store = new FileBackupSnapshotStore(Path.Combine(root, "snapshots")); var jobId = Guid.NewGuid();
        await store.SaveAsync(jobId, manifest);
        Assert.True(new FileInfo(Path.Combine(root, "snapshots", jobId.ToString("N") + ".json")).Length > 32 * 1024 * 1024);
        var restored = await store.ReadAsync(jobId); Assert.NotNull(restored);
        Assert.Equal(files.Length, restored.Files.Count); Assert.Equal(skipped.Length, restored.SkippedPaths!.Count);
        Assert.Equal(manifest.GetSnapshot(), restored.GetSnapshot());
    }
    private sealed class Denied : IBackupExecutor
    { public Task<string> CreateAsync(BackupJob job, string work, CancellationToken token) => throw new UnauthorizedAccessException(); }
    [Fact]
    [Trait("Category", "LargeFileBackup")]
    public async Task NormalAndPartialArchivesBothHandle100001RealSourceFiles()
    {
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        for (var index = 0; index < 100001; index++) File.WriteAllBytes(Path.Combine(source, index.ToString("D6") + ".txt"), []);
        var blocked = Path.Combine(source, "blocked"); Directory.CreateDirectory(blocked);
        File.WriteAllText(Path.Combine(blocked, "secret.txt"), "not in partial archive");
        var job = new BackupJob { Name = "Large source", Kind = DatabaseKind.FilesAndFolders, Source = source,
            DestinationDirectory = Path.Combine(root, "copies") };
        var executor = new FileFolderBackupExecutor(new BackupCatalog(Path.Combine(root, "catalog.json")),
            new FileBackupSnapshotStore(Path.Combine(root, "snapshots")));
        var normal = await executor.CreateAsync(job, Path.Combine(root, "full-work"), CancellationToken.None);
        IEnumerable<string> Enumerate(string path) => path == blocked ? throw new UnauthorizedAccessException() : Directory.EnumerateFileSystemEntries(path);
        var partial = await new AccessibleFilesBackupExecutor(new Denied(), enumerateSource: Enumerate)
            .CreateAsync(job, Path.Combine(root, "partial-work"), CancellationToken.None);
        foreach (var (path, incomplete, count) in new[] { (normal, false, 100002), (partial, true, 100001) })
        {
            using var zip = ZipFile.OpenRead(path);
            using var stream = zip.GetEntry("onec-backup-manifest.json")!.Open();
            var manifest = JsonSerializer.Deserialize<FileBackupManifest>(stream)!; manifest.Validate();
            Assert.Equal(count, manifest.Files.Count); Assert.Equal(incomplete, manifest.GetSnapshot().IsIncomplete);
            Assert.Equal(count + 1, zip.Entries.Count);
            Assert.Equal(incomplete, zip.GetEntry("data/blocked/secret.txt") is null);
        }
    }
}
