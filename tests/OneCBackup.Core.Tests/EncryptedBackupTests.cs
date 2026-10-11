using System.Security.Cryptography;
using System.Text.Json;
using ICSharpCode.SharpZipLib.Zip;
using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class EncryptedBackupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OneCBackup-encrypted-" + Guid.NewGuid());
    private const string Password = "Тестовый пароль 🗝 2026";
    public EncryptedBackupTests() => Directory.CreateDirectory(root);
    private sealed class Passwords : IArchivePasswordStore
    {
        public Task<string> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(key == "old-key" ? Password : "New password");
    }
    private readonly Guid jobId = Guid.NewGuid();
    private BackupJob Job => new() { Id = jobId, Name = "fixture", Source = root, DestinationDirectory = Path.Combine(root, "copies"),
        OneCMethod = OneCBackupMethod.FileArchive, ArchivePasswordKey = "old-key" };

    [Theory]
    [InlineData("База.dt", 0)]
    [InlineData("База.dt", 250000)]
    [InlineData("database.bak", 250000)]
    [InlineData("database.dump", 250000)]
    public async Task Aes256ArchiveRoundTripsAndRequiresPassword(string name, int size)
    {
        var bytes = new byte[size]; Random.Shared.NextBytes(bytes);
        var source = Path.Combine(root, name);
        await File.WriteAllBytesAsync(source, bytes);
        var path = await new BackupArchiver().CreateZipAsync(source, source + ".zip", password: Password);
        using var zip = new ZipFile(path) { Password = Password };
        Assert.Equal(256, zip[0].AESKeySize); Assert.True(zip[0].IsCrypted);
        Assert.Equal(name, zip[0].Name);
        using var restored = new MemoryStream();
        await using (var content = zip.GetInputStream(0)) await content.CopyToAsync(restored);
        Assert.Equal(bytes, restored.ToArray());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
        using var withoutPassword = new ZipFile(path);
        Assert.Throws<ZipException>(() => withoutPassword.GetInputStream(0));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    private async Task<BackupRecord> CreateFileCopy()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "1Cv8.1CD"), "closed file database bytes");
        var work = Path.Combine(root, "work"); Directory.CreateDirectory(work);
        var path = await new OneCFileArchiveExecutor(new Passwords()).CreateAsync(Job, work, default);
        return (await new BackupCompletion(new FileBackupStorage(), new BackupCatalog(Path.Combine(root, "copies.json")), new Passwords())
            .CompleteAsync(Job, path, Path.Combine(root, "staging"), DateTimeOffset.UtcNow)).Record;
    }

    [Theory]
    [InlineData(DatabaseKind.OneCFile, "backup.dt")]
    [InlineData(DatabaseKind.SqlServer, "backup.bak")]
    [InlineData(DatabaseKind.PostgreSql, "backup.dump")]
    public async Task DeliveryEncryptsDumpAndRegistersPasswordReference(DatabaseKind kind, string name)
    {
        var source = Path.Combine(root, name); await File.WriteAllTextAsync(source, "verified dump bytes");
        var job = Job with { Kind = kind, OneCMethod = OneCBackupMethod.PlatformDump, ArchiveCopies = true };
        var record = (await new BackupCompletion(new FileBackupStorage(), new BackupCatalog(Path.Combine(root, "copies.json")), new Passwords())
            .CompleteAsync(job, source, Path.Combine(root, "staging"), DateTimeOffset.UtcNow)).Record;
        Assert.Equal("old-key", record.Backup.ArchivePasswordKey);
        using var zip = new ZipFile(record.Backup.Location) { Password = Password };
        Assert.Equal(256, zip[0].AESKeySize); Assert.Equal(name, zip[0].Name);
        using var reader = new StreamReader(zip.GetInputStream(0));
        Assert.Equal("verified dump bytes", await reader.ReadToEndAsync());
        Assert.Equal("verified dump bytes", await File.ReadAllTextAsync(source));
        await new BackupCopyVerifier(new FileBackupStorage()).VerifyAsync(record, job);
    }

    [Fact]
    public async Task PlainZipCannotBeDeliveredAsEncryptedFileBase()
    {
        var source = Path.Combine(root, "1Cv8.1CD"); await File.WriteAllTextAsync(source, "database");
        var zip = await new BackupArchiver().CreateZipAsync(source, source + ".zip");
        await Assert.ThrowsAsync<InvalidDataException>(() => new BackupDelivery(new FileBackupStorage(), new Passwords())
            .DeliverAsync(Job, zip, Path.Combine(root, "staging")));
        Assert.False(Directory.Exists(Job.DestinationDirectory));
    }

    [Fact]
    public async Task PasswordRotationPreservesOldKeyInCatalogAndExtractionUsesIt()
    {
        var record = await CreateFileCopy();
        var loaded = Assert.Single(await new BackupCatalog(Path.Combine(root, "copies.json")).ReadAsync());
        Assert.Equal("old-key", loaded.Backup.ArchivePasswordKey);
        Directory.CreateDirectory(Path.Combine(root, "restore"));
        var result = await new OneCArchiveExtractor(new Passwords()).ExtractAsync(record,
            Job with { ArchivePasswordKey = "new-key" }, Path.Combine(root, "restore"));
        Assert.Equal("closed file database bytes", await File.ReadAllTextAsync(result.Path));
        Assert.DoesNotContain(Password, await File.ReadAllTextAsync(Path.Combine(root, "copies.json")));
        Assert.DoesNotContain(Password, JsonSerializer.Serialize(Job));
    }

    [Fact]
    public async Task WrongPasswordDoesNotPublishExtractedFileAndCorrectOverrideWorks()
    {
        var record = await CreateFileCopy();
        var parent = Path.Combine(root, "restore"); Directory.CreateDirectory(parent);
        await Assert.ThrowsAsync<InvalidDataException>(() => new OneCArchiveExtractor().ExtractAsync(record, Job, parent,
            passwordOverride: "incorrect"));
        Assert.Empty(Directory.GetFileSystemEntries(parent));
        var result = await new OneCArchiveExtractor().ExtractAsync(record, Job, parent, passwordOverride: Password);
        Assert.Equal("closed file database bytes", await File.ReadAllTextAsync(result.Path));
    }

    [Fact]
    public async Task TamperedAuthenticationCodeRejectedEvenWithMatchingCatalogHash()
    {
        var record = await CreateFileCopy();
        var bytes = await File.ReadAllBytesAsync(record.Backup.Location);
        using (var zip = new ZipFile(record.Backup.Location))
        {
            // Locate the AES authentication code before the central directory.
            var nameLength = BitConverter.ToUInt16(bytes, 26);
            var extraLength = BitConverter.ToUInt16(bytes, 28);
            var end = checked(30 + nameLength + extraLength + (int)zip[0].CompressedSize);
            bytes[end - 1] ^= 1;
        }
        await File.WriteAllBytesAsync(record.Backup.Location, bytes);
        record = record with { Backup = record.Backup with { Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) } };
        var parent = Path.Combine(root, "restore"); Directory.CreateDirectory(parent);
        await Assert.ThrowsAsync<InvalidDataException>(() => new OneCArchiveExtractor(new Passwords()).ExtractAsync(record, Job, parent));
        Assert.Empty(Directory.GetFileSystemEntries(parent));
    }

    [Fact]
    public async Task CancelledEncryptionRemovesPartialAndKeepsOriginal()
    {
        var source = Path.Combine(root, "backup.dump"); await File.WriteAllBytesAsync(source, new byte[300000]);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BackupArchiver().CreateZipAsync(source, source + ".zip",
            new CancelProgress(cancellation), cancellation.Token, Password));
        Assert.False(File.Exists(source + ".zip")); Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        Assert.Equal(300000, new FileInfo(source).Length);
    }
    private sealed class CancelProgress(CancellationTokenSource source) : IProgress<long>
    { public void Report(long value) => source.Cancel(); }

    [Fact]
    public async Task MissingPasswordFailsBeforeExecutorAndHistoryContainsNoSecret()
    {
        var executor = new NeverExecute(); var history = new BackupRunHistory(Path.Combine(root, "runs.json"));
        var completion = new BackupCompletion(new FileBackupStorage(), new BackupCatalog(Path.Combine(root, "copies.json")), new MissingPassword());
        var result = await new LocalBackupRunner(executor, completion, history, Path.Combine(root, "work"))
            .RunAsync(Job);
        Assert.Equal(BackupRunStatus.Failed, result.Status); Assert.False(executor.Executed);
        Assert.Empty(await new BackupCatalog(Path.Combine(root, "copies.json")).ReadAsync());
    }
    private sealed class NeverExecute : IBackupExecutor
    {
        public bool Executed;
        public Task<string> CreateAsync(BackupJob job, string workDirectory, CancellationToken cancellationToken)
        { Executed = true; throw new InvalidOperationException("Must not execute"); }
    }
    private sealed class MissingPassword : IArchivePasswordStore
    { public Task<string> GetAsync(string key, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Missing archive password"); }

    [Theory]
    [InlineData("")]
    [InlineData("line\nbreak")]
    public async Task InvalidPasswordDoesNotCreateArchive(string password)
    {
        var source = Path.Combine(root, "backup.dump"); await File.WriteAllTextAsync(source, "copy");
        await Assert.ThrowsAsync<ArgumentException>(() => new BackupArchiver().CreateZipAsync(source, source + ".zip", password: password));
        Assert.Single(Directory.GetFiles(root));
    }

    [Fact]
    public void EncryptionCannotSilentlyDeliverUnarchivedDump()
    {
        Assert.NotEmpty((Job with { Kind = DatabaseKind.PostgreSql, ArchiveCopies = false }).Validate());
        Assert.Empty((Job with { Kind = DatabaseKind.PostgreSql, ArchiveCopies = true }).Validate());
        Assert.Empty(Job.Validate());
    }
    public void Dispose() => Directory.Delete(root, recursive: true);
}
