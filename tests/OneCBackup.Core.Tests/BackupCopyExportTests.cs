using System.Security.Cryptography;
using System.Text.Json;
using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class BackupCopyExportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OneCBackup-export-" + Guid.NewGuid());
    private readonly BackupJob job;
    private readonly BackupCatalog catalog;
    public BackupCopyExportTests()
    {
        Directory.CreateDirectory(root);
        job = new() { Id = Guid.NewGuid(), Name = "Export", Source = "fixture", DestinationDirectory = Path.Combine(root, "copies") };
        catalog = new(Path.Combine(root, "catalog.json"));
    }
    private async Task<BackupRecord> CopyAsync()
    {
        var source = Path.Combine(root, "database.bak");
        await File.WriteAllTextAsync(source, "stable backup bytes");
        var stored = await new FileBackupStorage().StoreAsync(source, job);
        return await catalog.RegisterAsync(job, stored, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task ExportsExactBytesWithDifferentNameWithoutChangingCatalogOrSource()
    {
        var record = await CopyAsync();
        var before = JsonSerializer.Serialize(await catalog.ReadAsync());
        var bytes = await File.ReadAllBytesAsync(record.Backup.Location);
        var destination = Path.Combine(root, "chosen-name.bak");
        var result = await new BackupCopyExporter(new FileBackupStorage()).ExportAsync(record, job, destination);
        Assert.Equal(destination, result.Path);
        Assert.Equal(record.Backup.Length, result.Length);
        Assert.Equal(record.Backup.Sha256, result.Sha256, ignoreCase: true);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(record.Backup.Location));
        Assert.Equal(before, JsonSerializer.Serialize(await catalog.ReadAsync()));
        Assert.Empty(Directory.GetFiles(root, "*.partial"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingFileOrDirectoryIsNeverReplaced(bool directory)
    {
        var record = await CopyAsync();
        var destination = Path.Combine(root, "existing");
        if (directory) Directory.CreateDirectory(destination);
        else await File.WriteAllTextAsync(destination, "keep this file");
        var storage = new NoReadStorage();
        await Assert.ThrowsAsync<IOException>(() => new BackupCopyExporter(storage).ExportAsync(record, job, destination));
        if (directory) Assert.True(Directory.Exists(destination));
        else Assert.Equal("keep this file", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.GetFiles(root, "*.partial"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongLengthOrHashDoesNotPublishOrLeaveTemporaryFile(bool lengthChanged)
    {
        var record = await CopyAsync();
        var bytes = await File.ReadAllBytesAsync(record.Backup.Location);
        if (lengthChanged) bytes = bytes.Concat(new byte[] { 1 }).ToArray();
        else bytes[0] ^= 1;
        await File.WriteAllBytesAsync(record.Backup.Location, bytes);
        var destination = Path.Combine(root, "bad.bak");
        await Assert.ThrowsAsync<InvalidDataException>(() => new BackupCopyExporter(new FileBackupStorage()).ExportAsync(record, job, destination));
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(root, "*.partial"));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(record.Backup.Location));
    }

    [Fact]
    public async Task DestinationCreatedDuringReadIsPreservedAndTemporaryFileRemoved()
    {
        var record = await CopyAsync();
        var destination = Path.Combine(root, "raced.bak");
        var storage = new StreamStorage { AfterRead = () => File.WriteAllTextAsync(destination, "foreign file") };
        await Assert.ThrowsAsync<IOException>(() => new BackupCopyExporter(storage).ExportAsync(record, job, destination));
        Assert.Equal("foreign file", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.GetFiles(root, "*.partial"));
    }

    [Fact]
    public async Task CancelledPartialReadDoesNotPublishAndKeepsForeignFiles()
    {
        var record = await CopyAsync();
        using var cancellation = new CancellationTokenSource();
        var destination = Path.Combine(root, "cancelled.bak");
        var foreign = Path.Combine(root, "unrelated.partial");
        await File.WriteAllTextAsync(foreign, "keep");
        var storage = new StreamStorage { PartialRead = true, AfterRead = () => { cancellation.Cancel(); return Task.CompletedTask; } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BackupCopyExporter(storage).ExportAsync(record, job, destination, cancellation.Token));
        Assert.False(File.Exists(destination));
        Assert.Equal(new[] { foreign }, Directory.GetFiles(root, "*.partial"));
        Assert.Equal("keep", await File.ReadAllTextAsync(foreign));
    }

    [Fact]
    public async Task FailedPartialTransferDoesNotPublishOrChangeOriginalCopy()
    {
        var record = await CopyAsync();
        var original = await File.ReadAllBytesAsync(record.Backup.Location);
        var destination = Path.Combine(root, "failed.bak");
        var storage = new StreamStorage { PartialRead = true, AfterRead = () => Task.FromException(new IOException("Transfer interrupted")) };
        await Assert.ThrowsAsync<IOException>(() => new BackupCopyExporter(storage).ExportAsync(record, job, destination));
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(root, "*.partial"));
        Assert.Equal(original, await File.ReadAllBytesAsync(record.Backup.Location));
    }

    [Fact]
    public async Task RelativePathAndMissingParentCannotAccessStorage()
    {
        var record = await CopyAsync();
        var exporter = new BackupCopyExporter(new NoReadStorage());
        await Assert.ThrowsAsync<ArgumentException>(() => exporter.ExportAsync(record, job, "relative.bak"));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => exporter.ExportAsync(record, job, Path.Combine(root, "missing", "backup.bak")));
        Assert.False(Directory.Exists(Path.Combine(root, "missing")));
        Assert.Empty(Directory.GetFiles(root, "*.partial"));
    }

    [Fact]
    public async Task ChangedDestinationForeignJobAndInvalidMetadataCannotReadStorage()
    {
        var record = await CopyAsync();
        var exporter = new BackupCopyExporter(new NoReadStorage());
        var destination = Path.Combine(root, "export.bak");
        await Assert.ThrowsAsync<InvalidDataException>(() => exporter.ExportAsync(record, job with { Id = Guid.NewGuid() }, destination));
        await Assert.ThrowsAsync<InvalidDataException>(() => exporter.ExportAsync(record, job with { DestinationDirectory = Path.Combine(root, "changed") }, destination));
        await Assert.ThrowsAsync<InvalidDataException>(() => exporter.ExportAsync(record with { Backup = record.Backup with { Sha256 = "bad" } }, job, destination));
        Assert.False(File.Exists(destination));
    }

    [Theory]
    [InlineData("..%5Coutside.bak")]
    [InlineData("nested%2Foutside.bak")]
    public async Task EncodedFtpSeparatorsCannotEscapeConfiguredDirectory(string leaf)
    {
        var ftp = job with { StorageKind = StorageKind.Ftp, DestinationDirectory = "/backups", Ftp = new() { Host = "localhost", CredentialKey = "fixture" } };
        var record = new BackupRecord(Guid.NewGuid(), ftp.Id, "ftp://localhost/backups/", DateTimeOffset.UtcNow,
            new("ftp://localhost/backups/" + leaf, 0, Convert.ToHexString(SHA256.HashData(Array.Empty<byte>()))));
        await Assert.ThrowsAsync<InvalidDataException>(() => new BackupCopyExporter(new NoReadStorage()).ExportAsync(record, ftp, Path.Combine(root, "outside.bak")));
    }

    private sealed class NoReadStorage : IBackupStorage
    {
        public Task PrepareAsync(BackupJob target, CancellationToken token = default) => throw new InvalidOperationException("Unexpected access");
        public Task<StoredBackup> StoreAsync(string path, BackupJob target, CancellationToken token = default) => throw new InvalidOperationException();
        public Task DeleteAsync(StoredBackup backup, BackupJob target, CancellationToken token = default) => throw new InvalidOperationException();
        public Task VerifyAsync(StoredBackup backup, BackupJob target, CancellationToken token = default) => throw new InvalidOperationException();
    }
    private sealed class StreamStorage : IBackupStorage
    {
        public Func<Task>? AfterRead { get; init; }
        public bool PartialRead { get; init; }
        public Task<StoredBackup> StoreAsync(string path, BackupJob target, CancellationToken token = default) => throw new InvalidOperationException();
        public Task DeleteAsync(StoredBackup backup, BackupJob target, CancellationToken token = default) => throw new InvalidOperationException();
        public Task VerifyAsync(StoredBackup backup, BackupJob target, CancellationToken token = default) => throw new InvalidOperationException();
        public async Task CopyToAsync(StoredBackup backup, BackupJob target, Stream destination, CancellationToken token = default)
        {
            if (PartialRead)
            {
                var bytes = await File.ReadAllBytesAsync(backup.Location, token);
                await destination.WriteAsync(bytes.AsMemory(0, 2), token);
            }
            else await new FileBackupStorage().CopyToAsync(backup, target, destination, token);
            if (AfterRead is not null) await AfterRead();
        }
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
