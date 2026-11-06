using System.Security.Cryptography;
using System.Text.Json;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class RetentionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OneCBackup-retention-" + Guid.NewGuid());
    private readonly Guid jobId = Guid.NewGuid();
    private BackupJob Job => new() { Id = jobId, Name = "Test", Source = "database",
        DestinationDirectory = Path.Combine(directory, "copies"), KeepLast = 2 };
    private string CatalogPath => Path.Combine(directory, "catalog.json");

    public RetentionTests() => Directory.CreateDirectory(directory);

    private async Task<CompletedBackup> Complete(BackupCompletion completion, BackupJob job, int number)
    {
        var source = Path.Combine(directory, $"source-{number}.bak");
        await File.WriteAllTextAsync(source, $"backup {number}");
        return await completion.CompleteAsync(job, source, Path.Combine(directory, "stage"),
            new DateTimeOffset(2026, 10, 10, 0, number, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task KeepsLatestNAndPreservesUnregisteredFiles(int keep, bool archive)
    {
        var job = Job with { KeepLast = keep, ArchiveCopies = archive };
        var catalog = new BackupCatalog(CatalogPath);
        var completion = new BackupCompletion(new FileBackupStorage(), catalog);
        Directory.CreateDirectory(job.DestinationDirectory);
        var unrelated = Path.Combine(job.DestinationDirectory, "unregistered.bak");
        await File.WriteAllTextAsync(unrelated, "do not delete");
        for (var i = 1; i <= 4; i++)
        {
            var result = await Complete(completion, job, i);
            Assert.Empty(result.Retention.Failures);
            Assert.True(File.Exists(Path.Combine(directory, $"source-{i}.bak")));
        }
        var records = await new BackupCatalog(CatalogPath).ReadAsync();
        Assert.Equal(keep, records.Count);
        Assert.Equal(keep + 1, Directory.GetFiles(job.DestinationDirectory).Length);
        Assert.All(records, r => Assert.True(File.Exists(r.Backup.Location)));
        Assert.Equal("do not delete", await File.ReadAllTextAsync(unrelated));
        Assert.Contains(records, r => r.Backup.Location.EndsWith("source-4.bak" + (archive ? ".zip" : "")));
    }

    [Fact]
    public async Task FailedDeliveryDoesNotTriggerRetention()
    {
        var catalog = new BackupCatalog(CatalogPath);
        var completion = new BackupCompletion(new FileBackupStorage(), catalog);
        var first = await Complete(completion, Job, 1);
        await Assert.ThrowsAsync<FileNotFoundException>(() => completion.CompleteAsync(Job with { KeepLast = 1 },
            Path.Combine(directory, "missing.bak"), directory, DateTimeOffset.UtcNow));
        Assert.Equal(first.Record, Assert.Single(await catalog.ReadAsync()));
        Assert.True(File.Exists(first.Record.Backup.Location));
    }

    [Fact]
    public async Task ModifiedOlderCopyIsPreservedAndFailureIsReported()
    {
        var catalog = new BackupCatalog(CatalogPath);
        var completion = new BackupCompletion(new FileBackupStorage(), catalog);
        var first = await Complete(completion, Job, 1);
        await File.WriteAllTextAsync(first.Record.Backup.Location, "changed by somebody else");
        var second = await Complete(completion, Job with { KeepLast = 1 }, 2);
        Assert.IsType<InvalidDataException>(Assert.Single(second.Retention.Failures).Error);
        Assert.Equal(2, (await catalog.ReadAsync()).Count);
        Assert.True(File.Exists(first.Record.Backup.Location));
        Assert.True(File.Exists(second.Record.Backup.Location));
    }

    [Fact]
    public async Task OtherJobsAndPreviousStorageAreNotPruned()
    {
        var catalog = new BackupCatalog(CatalogPath);
        var completion = new BackupCompletion(new FileBackupStorage(), catalog);
        var first = await Complete(completion, Job, 1);
        var other = await Complete(completion, Job with { Id = Guid.NewGuid() }, 2);
        var moved = Job with { DestinationDirectory = Path.Combine(directory, "new-copies"), KeepLast = 1 };
        await Complete(completion, moved, 3);
        await Complete(completion, moved, 4);
        Assert.True(File.Exists(first.Record.Backup.Location));
        Assert.True(File.Exists(other.Record.Backup.Location));
        Assert.Equal(3, (await catalog.ReadAsync()).Count);
    }

    [Fact]
    public async Task MissingNewestCopyPreventsDeletionOfOlderCopies()
    {
        var catalog = new BackupCatalog(CatalogPath);
        var completion = new BackupCompletion(new FileBackupStorage(), catalog);
        var first = await Complete(completion, Job, 1);
        var second = await Complete(completion, Job, 2);
        File.Delete(second.Record.Backup.Location);
        await Assert.ThrowsAsync<FileNotFoundException>(() => new BackupRetention(catalog, new FileBackupStorage())
            .ApplyAsync(Job with { KeepLast = 1 }, second.Record));
        Assert.True(File.Exists(first.Record.Backup.Location));
        Assert.Equal(2, (await catalog.ReadAsync()).Count);
    }

    [Fact]
    public async Task UnregisteredSuccessCannotAuthorizeDeletion()
    {
        var catalog = new BackupCatalog(CatalogPath);
        var completion = new BackupCompletion(new FileBackupStorage(), catalog);
        var first = await Complete(completion, Job, 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => new BackupRetention(catalog, new FileBackupStorage())
            .ApplyAsync(Job with { KeepLast = 1 }, first.Record with { Id = Guid.NewGuid() }));
        Assert.True(File.Exists(first.Record.Backup.Location));
    }

    [Fact]
    public async Task DamagedCatalogStopsCleanup()
    {
        var catalog = new BackupCatalog(CatalogPath);
        var completion = new BackupCompletion(new FileBackupStorage(), catalog);
        var first = await Complete(completion, Job, 1);
        await File.WriteAllTextAsync(CatalogPath, "{broken JSON");
        await Assert.ThrowsAsync<JsonException>(() => Complete(completion, Job with { KeepLast = 1 }, 2));
        Assert.True(File.Exists(first.Record.Backup.Location));
        Assert.Equal("{broken JSON", await File.ReadAllTextAsync(CatalogPath));
    }

    [Fact]
    public async Task FilesOutsideConfiguredDirectoryCannotBeDeleted()
    {
        var outside = Path.Combine(directory, "outside.bak");
        var contents = new byte[] { 1, 2, 3 };
        await File.WriteAllBytesAsync(outside, contents);
        var record = new StoredBackup(outside, contents.Length, Convert.ToHexString(SHA256.HashData(contents)));
        await Assert.ThrowsAsync<InvalidDataException>(() => new FileBackupStorage().DeleteAsync(record, Job));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void KeepingZeroCopiesIsRejected() => Assert.NotEmpty((Job with { KeepLast = 0 }).Validate());

    public void Dispose() => Directory.Delete(directory, true);
}
