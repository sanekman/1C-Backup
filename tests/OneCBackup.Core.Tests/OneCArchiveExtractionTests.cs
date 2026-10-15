using System.IO.Compression;
using System.Security.Cryptography;
using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class OneCArchiveExtractionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OneCBackup-extract-" + Guid.NewGuid());
    private BackupJob Job { get; }
    private string Parent => Path.Combine(root, "existing-bases");
    public OneCArchiveExtractionTests()
    {
        Job = new BackupJob { Name = "File base", Source = Path.Combine(root, "source"),
            DestinationDirectory = Path.Combine(root, "copies"), OneCMethod = OneCBackupMethod.FileArchive };
        Directory.CreateDirectory(Job.Source);
        Directory.CreateDirectory(Job.DestinationDirectory);
        Directory.CreateDirectory(Parent);
    }

    private async Task<BackupRecord> Record(params (string Name, string Contents)[] entries)
    {
        var path = Path.Combine(Job.DestinationDirectory, Guid.NewGuid() + ".1CD.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var entry in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(entry.Name).Open());
                writer.Write(entry.Contents);
            }
        await using var stream = File.OpenRead(path);
        var backup = new StoredBackup(path, stream.Length, Convert.ToHexString(await SHA256.HashDataAsync(stream)));
        return await new BackupCatalog(Path.Combine(root, "copies.json")).RegisterAsync(Job, backup, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task ExtractsIntoNewFoldersAndPreservesExistingBaseArchiveAndCatalog()
    {
        var original = Path.Combine(Parent, "1Cv8.1CD");
        await File.WriteAllTextAsync(original, "existing base must remain");
        var record = await Record(("1Cv8.1CD", "archived database bytes"));
        var first = await new OneCArchiveExtractor().ExtractAsync(record, Job, Parent);
        var second = await new OneCArchiveExtractor().ExtractAsync(record, Job, Parent);
        Assert.NotEqual(first.Path, second.Path);
        Assert.Equal("archived database bytes", await File.ReadAllTextAsync(first.Path));
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(first.Path))), first.Sha256);
        Assert.Equal(new FileInfo(first.Path).Length, first.Length);
        Assert.Equal("existing base must remain", await File.ReadAllTextAsync(original));
        await new BackupCopyVerifier(new FileBackupStorage()).VerifyAsync(record, Job);
        Assert.Equal(record, Assert.Single(await new BackupCatalog(Path.Combine(root, "copies.json")).ReadAsync()));
        Assert.DoesNotContain(Directory.GetFiles(Parent, "*", SearchOption.AllDirectories), path => path.EndsWith(".partial"));
    }

    [Fact]
    public async Task ChangedArchiveIsRejectedBeforeCreatingDestination()
    {
        var record = await Record(("1Cv8.1CD", "database bytes"));
        var bytes = await File.ReadAllBytesAsync(record.Backup.Location);
        bytes[0] ^= 1;
        await File.WriteAllBytesAsync(record.Backup.Location, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => new OneCArchiveExtractor().ExtractAsync(record, Job, Parent));
        Assert.Empty(Directory.GetFileSystemEntries(Parent));
    }

    [Theory]
    [InlineData("../1Cv8.1CD", "bytes")]
    [InlineData("folder/1Cv8.1CD", "bytes")]
    [InlineData("other.bak", "bytes")]
    [InlineData("1Cv8.1CD", "")]
    public async Task UnexpectedEntriesAndEmptyBaseAreRejected(string name, string contents)
    {
        var record = await Record((name, contents));
        await Assert.ThrowsAsync<InvalidDataException>(() => new OneCArchiveExtractor().ExtractAsync(record, Job, Parent));
        Assert.Empty(Directory.GetFileSystemEntries(Parent));
    }

    [Fact]
    public async Task MultipleEntriesAreRejected()
    {
        var record = await Record(("1Cv8.1CD", "base"), ("extra.txt", "extra"));
        await Assert.ThrowsAsync<InvalidDataException>(() => new OneCArchiveExtractor().ExtractAsync(record, Job, Parent));
        Assert.Empty(Directory.GetFileSystemEntries(Parent));
    }

    [Fact]
    public async Task OwnershipMismatchAndPreCancelledExtractionDoNotTouchDestination()
    {
        var record = await Record(("1Cv8.1CD", "base"));
        var extractor = new OneCArchiveExtractor();
        await Assert.ThrowsAsync<InvalidDataException>(() => extractor.ExtractAsync(record with { JobId = Guid.NewGuid() }, Job, Parent));
        await Assert.ThrowsAsync<InvalidDataException>(() => extractor.ExtractAsync(record, Job with { DestinationDirectory = Parent }, Parent));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extractor.ExtractAsync(record, Job, Parent, cancelled.Token));
        Assert.Empty(Directory.GetFileSystemEntries(Parent));
        Assert.True(File.Exists(record.Backup.Location));
    }

    public void Dispose() => Directory.Delete(root, true);
}
