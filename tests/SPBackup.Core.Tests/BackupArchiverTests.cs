using System.IO.Compression;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class BackupArchiverTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OneCBackup-zip-" + Guid.NewGuid());

    public BackupArchiverTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData("Бухгалтерия.dt", 0)]
    [InlineData("Бухгалтерия.dt", 200000)]
    [InlineData("database.bak", 200000)]
    [InlineData("database.dump", 200000)]
    public async Task ArchiveRestoresExactBackupAndPreservesSource(string name, int size)
    {
        var source = Path.Combine(directory, name);
        var contents = new byte[size];
        new Random(123).NextBytes(contents);
        await File.WriteAllBytesAsync(source, contents);
        var job = new BackupJob { ArchiveCopies = true, DestinationDirectory = Path.Combine(directory, "archives") };

        var path = await new BackupArchiver().PrepareAsync(job, source);

        Assert.Equal(Path.Combine(job.DestinationDirectory, name + ".zip"), path);
        using var zip = ZipFile.OpenRead(path);
        var entry = Assert.Single(zip.Entries);
        Assert.Equal(name, entry.FullName);
        using var restored = new MemoryStream();
        await using (var stream = entry.Open()) await stream.CopyToAsync(restored);
        Assert.Equal(contents, restored.ToArray());
        Assert.Equal(contents, await File.ReadAllBytesAsync(source));
        Assert.Empty(Directory.GetFiles(job.DestinationDirectory, "*.tmp"));
    }

    [Fact]
    public async Task DisabledArchivingReturnsSourceWithoutCreatingFiles()
    {
        var source = Path.Combine(directory, "backup.dt");
        await File.WriteAllTextAsync(source, "copy");
        Assert.Equal(source, await new BackupArchiver().PrepareAsync(new BackupJob(), source));
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task ExistingArchiveIsPreserved()
    {
        var source = Path.Combine(directory, "backup.bak");
        var archive = source + ".zip";
        await File.WriteAllTextAsync(source, "new backup");
        await File.WriteAllTextAsync(archive, "existing archive");
        await Assert.ThrowsAsync<IOException>(() => new BackupArchiver().CreateZipAsync(source, archive));
        Assert.Equal("existing archive", await File.ReadAllTextAsync(archive));
        Assert.Equal("new backup", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task CancellationDuringPackingRemovesPartialArchiveAndPreservesSource()
    {
        var source = Path.Combine(directory, "backup.dump");
        var contents = new byte[300000];
        await File.WriteAllBytesAsync(source, contents);
        using var cancellation = new CancellationTokenSource();
        var progress = new CancelOnProgress(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BackupArchiver().CreateZipAsync(
            source, source + ".zip", progress, cancellation.Token));
        Assert.False(File.Exists(source + ".zip"));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        Assert.Equal(contents, await File.ReadAllBytesAsync(source));
    }

    [Fact]
    public async Task MissingSourceDoesNotCreateArchive()
    {
        var source = Path.Combine(directory, "missing.bak");
        await Assert.ThrowsAsync<FileNotFoundException>(() => new BackupArchiver().CreateZipAsync(source, source + ".zip"));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task SourceCannotBeUsedAsArchivePath()
    {
        var source = Path.Combine(directory, "backup.dt");
        await File.WriteAllTextAsync(source, "backup");
        await Assert.ThrowsAsync<ArgumentException>(() => new BackupArchiver().CreateZipAsync(source, source));
        Assert.Equal("backup", await File.ReadAllTextAsync(source));
    }

    private sealed class CancelOnProgress(CancellationTokenSource cancellation) : IProgress<long>
    {
        public void Report(long value) => cancellation.Cancel();
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
