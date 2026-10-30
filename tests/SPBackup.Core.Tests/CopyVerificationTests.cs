using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class CopyVerificationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OneCBackup-inspection-" + Guid.NewGuid());
    private readonly BackupJob job;
    private readonly BackupCatalog catalog;

    public CopyVerificationTests()
    {
        Directory.CreateDirectory(directory);
        job = new BackupJob { Name = "Бухгалтерия", Source = "database", DestinationDirectory = Path.Combine(directory, "copies") };
        catalog = new BackupCatalog(Path.Combine(directory, "catalog.json"));
    }

    private async Task<BackupRecord> CreateCopy()
    {
        var source = Path.Combine(directory, "original.bak");
        await File.WriteAllTextAsync(source, "valid backup content");
        var stored = await new FileBackupStorage().StoreAsync(source, job);
        return await catalog.RegisterAsync(job, stored, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task VerifiesRealCopyWithoutChangingCatalogOrFile()
    {
        var record = await CreateCopy();
        var originalCatalog = await File.ReadAllTextAsync(Path.Combine(directory, "catalog.json"));
        await new BackupCopyVerifier(new FileBackupStorage()).VerifyAsync(record, job);
        Assert.Equal("valid backup content", await File.ReadAllTextAsync(record.Backup.Location));
        Assert.Equal(originalCatalog, await File.ReadAllTextAsync(Path.Combine(directory, "catalog.json")));
    }

    [Fact]
    public async Task DetectsCorruptionOfSameLengthAndPreservesRecordAndFile()
    {
        var record = await CreateCopy();
        await File.WriteAllTextAsync(record.Backup.Location, "VALID BACKUP CONTENT");
        Assert.Equal(record.Backup.Length, new FileInfo(record.Backup.Location).Length);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new BackupCopyVerifier(new FileBackupStorage()).VerifyAsync(record, job));
        Assert.Equal(record, Assert.Single(await catalog.ReadAsync()));
        Assert.Equal("VALID BACKUP CONTENT", await File.ReadAllTextAsync(record.Backup.Location));
    }

    [Theory]
    [InlineData("different-job")]
    [InlineData("different-storage")]
    [InlineData("outside-directory")]
    public async Task RejectsMismatchedOwnershipBeforeTouchingStorage(string change)
    {
        var record = await CreateCopy();
        var target = change == "different-job" ? job with { Id = Guid.NewGuid() }
            : change == "different-storage" ? job with { DestinationDirectory = Path.Combine(directory, "elsewhere") } : job;
        if (change == "outside-directory")
            record = record with { Backup = record.Backup with { Location = Path.Combine(directory, "unrelated.bak") } };
        await Assert.ThrowsAsync<InvalidDataException>(() => new BackupCopyVerifier(new NoAccessStorage()).VerifyAsync(record, target));
    }

    [Fact]
    public async Task CancellationBeforeVerificationDoesNotTouchStorage()
    {
        var record = await CreateCopy();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new BackupCopyVerifier(new NoAccessStorage()).VerifyAsync(record, job, cancellation.Token));
    }

    private sealed class NoAccessStorage : IBackupStorage
    {
        public Task PrepareAsync(BackupJob job, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected storage access");
        public Task<StoredBackup> StoreAsync(string path, BackupJob job, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected write");
        public Task DeleteAsync(StoredBackup backup, BackupJob job, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected deletion");
        public Task VerifyAsync(StoredBackup backup, BackupJob job, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected storage access");
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
