using System.Net;
using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class RoutedStorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OneCBackup-route-" + Guid.NewGuid());
    private BackupJob Job => new() { Name = "Route", Source = "database", DestinationDirectory = Path.Combine(root, "copies") };
    private BackupJob FtpJob => Job with { StorageKind = StorageKind.Ftp, DestinationDirectory = "/copies",
        Ftp = new FtpSettings { Host = "ftp.example.com", CredentialKey = "vault-key" } };

    public RoutedStorageTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task LocalDeliveryNeverReadsFtpCredentials()
    {
        var credentials = new Credentials { Missing = true };
        var router = new RoutedBackupStorage(credentials, Path.Combine(root, "verify"));
        var source = Path.Combine(root, "database.bak");
        await File.WriteAllTextAsync(source, "backup fixture");
        await router.PrepareAsync(Job);
        var backup = await router.StoreAsync(source, Job);
        await router.VerifyAsync(backup, Job);
        await router.DeleteAsync(backup, Job);
        Assert.Equal(0, credentials.Reads);
        Assert.False(File.Exists(backup.Location));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task FtpOperationsUseCredentialKeyAndCorrectStorageAdapter()
    {
        var credentials = new Credentials();
        var remote = new RecordingStorage();
        var router = new RoutedBackupStorage(credentials, Path.Combine(root, "verify"), (login, directory) =>
        {
            Assert.Equal("fixture-user", login.UserName);
            Assert.Equal("fixture-password", login.Password);
            Assert.Equal(Path.Combine(root, "verify"), directory);
            return remote;
        });
        await router.PrepareAsync(FtpJob);
        var stored = await router.StoreAsync("completed.bak", FtpJob);
        await router.VerifyAsync(stored, FtpJob);
        await router.DeleteAsync(stored, FtpJob);
        Assert.Equal("vault-key", credentials.LastKey);
        Assert.Equal(new[] { "store", "verify", "delete" }, remote.Calls);
    }

    [Fact]
    public async Task MissingCredentialPreventsDatabaseExecutionAndIsRecordedWithoutPassword()
    {
        var credentials = new Credentials { Missing = true };
        var router = new RoutedBackupStorage(credentials, Path.Combine(root, "verify"));
        var history = new BackupRunHistory(Path.Combine(root, "runs.json"));
        var executor = new ForbiddenExecutor();
        var runner = new LocalBackupRunner(executor,
            new BackupCompletion(router, new BackupCatalog(Path.Combine(root, "catalog.json"))), history, Path.Combine(root, "work"));
        var result = await runner.RunAsync(FtpJob);
        Assert.Equal(BackupRunStatus.Failed, result.Status);
        Assert.False(executor.Called);
        Assert.Equal(BackupRunStatus.Failed, Assert.Single(await history.ReadAsync()).Status);
        Assert.DoesNotContain("fixture-password", await File.ReadAllTextAsync(Path.Combine(root, "runs.json")));
        await new JobStore(Path.Combine(root, "jobs.json")).SaveAsync([FtpJob]);
        var json = await File.ReadAllTextAsync(Path.Combine(root, "jobs.json"));
        Assert.DoesNotContain("fixture-password", json);
        Assert.DoesNotContain("fixture-user", json);
    }

    [Fact]
    public async Task FtpVerificationAndRetentionUseSelectedTemporaryFolder()
    {
        var temporary = Path.Combine(root, "HDD temporary");
        var remote = new RecordingStorage();
        var router = new RoutedBackupStorage(new Credentials(), Path.Combine(root, "default verify"), (_, directory) =>
        {
            Assert.Equal(Path.Combine(temporary, ".onec-backup-ftp-verification"), directory);
            return remote;
        });
        var job = FtpJob with { TemporaryDirectory = temporary };
        await router.PrepareAsync(job);
        var stored = await router.StoreAsync("completed.bak", job);
        await router.VerifyAsync(stored, job);
        await router.DeleteAsync(stored, job);
        Assert.Equal(new[] { "store", "verify", "delete" }, remote.Calls);
        Assert.False(Directory.Exists(Path.Combine(root, "default verify")));
    }

    [Fact]
    public async Task InvalidCredentialsAreRejectedBeforeFtpAdapterIsCreated()
    {
        var credentials = new Credentials { Username = "user\r\nPASS injected" };
        var router = new RoutedBackupStorage(credentials, root, (_, _) => throw new Exception("Must not create FTP adapter"));
        await Assert.ThrowsAsync<InvalidDataException>(() => router.PrepareAsync(FtpJob));
    }

    [Fact]
    public async Task CancelledPreparationDoesNotReadCredentials()
    {
        var credentials = new Credentials();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RoutedBackupStorage(credentials, root)
            .PrepareAsync(FtpJob, cancellation.Token));
        Assert.Equal(0, credentials.Reads);
    }

    private sealed class Credentials : IFtpCredentialStore
    {
        public bool Missing { get; init; }
        public string Username { get; init; } = "fixture-user";
        public int Reads { get; private set; }
        public string? LastKey { get; private set; }
        public Task<NetworkCredential> GetAsync(string key, CancellationToken cancellationToken)
        {
            Reads++; LastKey = key;
            if (Missing) throw new InvalidOperationException("Не сохранены учётные данные FTP: " + key);
            return Task.FromResult(new NetworkCredential(Username, "fixture-password"));
        }
    }
    private sealed class RecordingStorage : IBackupStorage
    {
        public List<string> Calls { get; } = [];
        public Task<StoredBackup> StoreAsync(string path, BackupJob job, CancellationToken token)
        { Calls.Add("store"); return Task.FromResult(new StoredBackup("ftps://ftp.example.com/copies/backup.bak", 1, new string('A', 64))); }
        public Task VerifyAsync(StoredBackup backup, BackupJob job, CancellationToken token)
        { Calls.Add("verify"); return Task.CompletedTask; }
        public Task DeleteAsync(StoredBackup backup, BackupJob job, CancellationToken token)
        { Calls.Add("delete"); return Task.CompletedTask; }
    }
    private sealed class ForbiddenExecutor : IBackupExecutor
    {
        public bool Called { get; private set; }
        public Task<string> CreateAsync(BackupJob job, string directory, CancellationToken token)
        { Called = true; throw new InvalidOperationException("Must not start database execution"); }
    }
    public void Dispose() => Directory.Delete(root, recursive: true);
}
