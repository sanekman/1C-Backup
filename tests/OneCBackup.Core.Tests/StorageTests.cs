using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OneCBackup-storage-" + Guid.NewGuid());
    public StorageTests() => Directory.CreateDirectory(directory);
    private BackupJob Job => new()
    {
        Name = "Тест", Source = "database", DestinationDirectory = Path.Combine(directory, "stored")
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliversExactBackupOrZipAndKeepsSource(bool archive)
    {
        var source = Path.Combine(directory, "backup.bak");
        var contents = new byte[200000];
        new Random(45).NextBytes(contents);
        await File.WriteAllBytesAsync(source, contents);
        var staging = Path.Combine(directory, "staging");
        var stored = await new BackupDelivery(new FileBackupStorage()).DeliverAsync(Job with { ArchiveCopies = archive }, source, staging);
        Assert.Equal(new FileInfo(stored.Location).Length, stored.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(stored.Location))), stored.Sha256);
        if (archive)
        {
            using var zip = ZipFile.OpenRead(stored.Location);
            var entry = Assert.Single(zip.Entries);
            using var restored = new MemoryStream();
            await using (var stream = entry.Open()) await stream.CopyToAsync(restored);
            Assert.Equal(contents, restored.ToArray());
            Assert.Empty(Directory.GetFileSystemEntries(staging));
        }
        else Assert.Equal(contents, await File.ReadAllBytesAsync(stored.Location));
        Assert.Equal(contents, await File.ReadAllBytesAsync(source));
        Assert.Empty(Directory.GetFiles(Job.DestinationDirectory, "*.tmp"));
    }

    [Fact]
    public async Task ExistingCopyIsNotOverwrittenAndStagingIsCleaned()
    {
        var source = Path.Combine(directory, "backup.dt");
        await File.WriteAllTextAsync(source, "backup");
        Directory.CreateDirectory(Job.DestinationDirectory);
        var existing = Path.Combine(Job.DestinationDirectory, "backup.dt.zip");
        await File.WriteAllTextAsync(existing, "previous copy");
        var staging = Path.Combine(directory, "staging");
        await Assert.ThrowsAsync<IOException>(() => new BackupDelivery(new FileBackupStorage()).DeliverAsync(
            Job with { ArchiveCopies = true }, source, staging));
        Assert.Equal("previous copy", await File.ReadAllTextAsync(existing));
        Assert.Equal("backup", await File.ReadAllTextAsync(source));
        Assert.Empty(Directory.GetFileSystemEntries(staging));
    }

    [Fact]
    public async Task CopyAlreadyInDestinationIsVerifiedInPlace()
    {
        Directory.CreateDirectory(Job.DestinationDirectory);
        var source = Path.Combine(Job.DestinationDirectory, "backup.dt");
        await File.WriteAllTextAsync(source, "backup");
        var result = await new FileBackupStorage().StoreAsync(source, Job);
        Assert.Equal(source, result.Location);
        Assert.Single(Directory.GetFiles(Job.DestinationDirectory));
    }

    [Fact]
    public async Task CancelledDeliveryDoesNotCreateDestination()
    {
        var source = Path.Combine(directory, "backup.dt");
        await File.WriteAllTextAsync(source, "backup");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FileBackupStorage().StoreAsync(source, Job, cancellation.Token));
        Assert.False(Directory.Exists(Job.DestinationDirectory));
        Assert.Equal("backup", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task LegacySettingsUseLocalStorage()
    {
        var settings = Path.Combine(directory, "jobs.json");
        await new JobStore(settings).SaveAsync([Job]);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(settings))!;
        document[0]!.AsObject().Remove("StorageKind");
        document[0]!.AsObject().Remove("Ftp");
        await File.WriteAllTextAsync(settings, document.ToJsonString());
        Assert.Equal(StorageKind.LocalDirectory, Assert.Single(await new JobStore(settings).LoadAsync()).StorageKind);
    }

    [Fact]
    public async Task FtpSettingsRoundTripWithoutCredentials()
    {
        var job = Job with { StorageKind = StorageKind.Ftp, DestinationDirectory = "/backups", Ftp = new()
        { Host = "ftp.example.com", CredentialKey = "backup-ftp", Port = 2121 } };
        var settings = Path.Combine(directory, "jobs.json");
        await new JobStore(settings).SaveAsync([job]);
        Assert.Equal(job, Assert.Single(await new JobStore(settings).LoadAsync()));
        Assert.DoesNotContain("Password", await File.ReadAllTextAsync(settings));
    }

    [Theory]
    [InlineData(@"\\server\share\backups", true)]
    [InlineData(@"Z:\Backups", false)]
    [InlineData(@"\\server", false)]
    public void NetworkShareRequiresUncPath(string path, bool valid)
    {
        Assert.Equal(valid, (Job with { StorageKind = StorageKind.NetworkShare, DestinationDirectory = path }).Validate().Count == 0);
    }

    [Theory]
    [InlineData("ftp://server/path", 21, "/backups")]
    [InlineData("server", 0, "/backups")]
    [InlineData("server", 21, "relative")]
    [InlineData("server", 21, "/../backups")]
    public void InvalidFtpConfigurationIsRejected(string host, int port, string path)
    {
        Assert.NotEmpty((Job with { StorageKind = StorageKind.Ftp, DestinationDirectory = path,
            Ftp = new() { Host = host, Port = port, CredentialKey = "fixture" } }).Validate());
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
