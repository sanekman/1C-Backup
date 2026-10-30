using System.Net;
using System.IO.Compression;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class FtpFactAttribute : FactAttribute
{
    public FtpFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ONECBACKUP_TEST_FTP_PORT") is null)
            Skip = "Запускается через tests/ftp/run_integration.py с локальным FTP-сервером.";
    }
}

public sealed class FtpTheoryAttribute : TheoryAttribute
{
    public FtpTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("ONECBACKUP_TEST_FTP_PORT") is null)
            Skip = "Запускается через tests/ftp/run_integration.py с локальным FTP-сервером.";
    }
}

[Trait("Category", "FtpIntegration")]
public sealed class FtpStorageIntegrationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OneCBackup-ftp-" + Guid.NewGuid());
    private readonly string remoteDirectory = "/" + Guid.NewGuid().ToString("N");
    private string RemoteRoot => Environment.GetEnvironmentVariable("ONECBACKUP_TEST_FTP_ROOT")!;
    private BackupJob Job => new()
    {
        Name = "FTP fixture", Source = "database", StorageKind = StorageKind.Ftp,
        DestinationDirectory = remoteDirectory, Ftp = new()
        {
            Host = "127.0.0.1", Port = int.Parse(Environment.GetEnvironmentVariable("ONECBACKUP_TEST_FTP_PORT")!),
            UseTls = false, CredentialKey = "local-fixture"
        }
    };
    // These credentials exist only on the disposable loopback test server.
    private FtpBackupStorage Storage(string password = "local-fixture-password") =>
        new(new NetworkCredential("backup-test", password), Path.Combine(directory, "verify"));

    public FtpStorageIntegrationTests() => Directory.CreateDirectory(directory);

    [FtpFact]
    public async Task FileIncrementalChainOnFtpRestoresChangesAndKeepsFullBasisBeyondLimit()
    {
        var source = Path.Combine(directory, "files"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "stable.txt"), "stable");
        await File.WriteAllTextAsync(Path.Combine(source, "changed.txt"), "before");
        var job = Job with { Kind = DatabaseKind.FilesAndFolders, Source = source, FileBackupMode = FileBackupMode.Incremental,
            FullBackupEvery = 7, KeepLast = 1, ScheduleKind = ScheduleKind.Manual,
            TemporaryDirectory = Path.Combine(directory, "hdd-work") };
        var catalog = new BackupCatalog(Path.Combine(directory, "file-copies.json"));
        var storage = Storage(); var runner = new LocalBackupRunner(new OneCFileArchiveExecutor(), new BackupCompletion(storage, catalog),
            new BackupRunHistory(Path.Combine(directory, "file-runs.json")), Path.Combine(directory, "default-work"));
        var full = await runner.RunAsync(job); Assert.True(full.Status == BackupRunStatus.Succeeded, full.Error);
        await File.WriteAllTextAsync(Path.Combine(source, "changed.txt"), "after");
        var incremental = await runner.RunAsync(job); Assert.True(incremental.Status == BackupRunStatus.Succeeded, incremental.Error);
        var records = await catalog.ReadAsync(); Assert.Equal(2, records.Count);
        var selected = records.Single(record => !record.Backup.FileSnapshot!.IsFull);
        var parent = Path.Combine(directory, "restore"); Directory.CreateDirectory(parent);
        var restored = await new FileBackupRestorer(storage, catalog, Path.Combine(directory, "default-work"))
            .RestoreAsync(selected, job, parent);
        Assert.Equal(2, restored.Archives); Assert.Equal("after", await File.ReadAllTextAsync(Path.Combine(restored.Directory, "changed.txt")));
        Assert.Equal("stable", await File.ReadAllTextAsync(Path.Combine(restored.Directory, "stable.txt")));
        Assert.Equal(2, (await catalog.ReadAsync()).Count);
    }

    [FtpFact]
    public async Task LocalRunnerAndScheduleDeliverFileDatabaseArchiveWithRoutedCredentialsAndRetention()
    {
        var source = Path.Combine(directory, "database");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "1Cv8.1CD"), "closed database fixture");
        var job = Job with { Source = source, OneCMethod = OneCBackupMethod.FileArchive, ArchiveCopies = true,
            KeepLast = 1, DailyAt = new TimeOnly(13, 0), TimeZoneId = "UTC" };
        var catalog = new BackupCatalog(Path.Combine(directory, "catalog.json"));
        var router = new RoutedBackupStorage(new FixtureCredentials(), Path.Combine(directory, "verify"));
        var runner = new LocalBackupRunner(new OneCFileArchiveExecutor(), new BackupCompletion(router, catalog),
            new BackupRunHistory(Path.Combine(directory, "runs.json")), Path.Combine(directory, "work"));
        Assert.Equal(BackupRunStatus.Succeeded, (await runner.RunAsync(job)).Status);
        var store = new JobStore(Path.Combine(directory, "jobs.json"));
        await store.SaveAsync([job]);
        var dispatcher = new LocalBackupDispatcher(store, async (snapshot, token) =>
            Assert.Equal(BackupRunStatus.Succeeded, (await runner.RunAsync(snapshot, token)).Status));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.RunScheduleAsync(
            _ => { cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token, timeProvider: new FixtureClock()));
        var record = Assert.Single(await catalog.ReadAsync());
        Assert.EndsWith(".1CD.zip", record.Backup.Location);
        var remote = Path.Combine(RemoteRoot, remoteDirectory.TrimStart('/'));
        var copy = Assert.Single(Directory.GetFiles(remote));
        using var zip = ZipFile.OpenRead(copy);
        var entry = Assert.Single(zip.Entries);
        Assert.Equal("1Cv8.1CD", entry.FullName);
        using var reader = new StreamReader(entry.Open());
        Assert.Equal("closed database fixture", await reader.ReadToEndAsync());
        Assert.Equal("closed database fixture", await File.ReadAllTextAsync(Path.Combine(source, "1Cv8.1CD")));
        Assert.Equal(2, (await new BackupRunHistory(Path.Combine(directory, "runs.json")).ReadAsync()).Count);
    }

    [FtpFact]
    public async Task OneArchiveReachesFtpAndLocalFolderWithIndependentRetention()
    {
        var source = Path.Combine(directory, "multi-database");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "1Cv8.1CD"), "multi-destination fixture");
        var localDirectory = Path.Combine(directory, "local-copies");
        var temporary = Path.Combine(directory, "HDD temporary");
        var job = Job with { Source = source, OneCMethod = OneCBackupMethod.FileArchive, KeepLast = 1,
            TemporaryDirectory = temporary,
            AdditionalDestinations = [new BackupDestination { DestinationDirectory = localDirectory, KeepLast = 2 }] };
        var catalog = new BackupCatalog(Path.Combine(directory, "multi-catalog.json"));
        var router = new RoutedBackupStorage(new FixtureCredentials(), Path.Combine(directory, "verify"));
        var runner = new LocalBackupRunner(new OneCFileArchiveExecutor(), new BackupCompletion(router, catalog),
            new BackupRunHistory(Path.Combine(directory, "multi-runs.json")), Path.Combine(directory, "work"));
        for (var index = 0; index < 3; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(source, "1Cv8.1CD"), "multi-destination fixture " + index);
            var run = await runner.RunAsync(job);
            Assert.Equal(BackupRunStatus.Succeeded, run.Status);
            Assert.Equal(temporary, Path.GetDirectoryName(run.WorkDirectory));
            Assert.False(Directory.Exists(run.WorkDirectory));
            Assert.Equal(2, run.Destinations!.Count);
        }
        var records = await catalog.ReadAsync();
        Assert.Equal(3, records.Count);
        var latestLocal = records.Where(record => Path.GetDirectoryName(record.Backup.Location) == localDirectory)
            .MaxBy(record => record.CompletedUtc)!;
        var remote = Assert.Single(records, record => record.Backup.Location.StartsWith("ftp", StringComparison.Ordinal));
        Assert.Equal(remote.Backup.Sha256, latestLocal.Backup.Sha256);
        Assert.Equal(2, records.Select(record => record.Backup.Sha256).Distinct().Count());
        var beforeAudit = System.Text.Json.JsonSerializer.Serialize(records);
        var audit = await new LatestBackupAudit(router, catalog).CheckAsync(job);
        Assert.False(audit.Cancelled);
        Assert.Equal(2, audit.Destinations.Count);
        Assert.All(audit.Destinations, result => Assert.Equal(BackupAuditStatus.Verified, result.Status));
        Assert.Equal(beforeAudit, System.Text.Json.JsonSerializer.Serialize(await catalog.ReadAsync()));
        var exported = await new BackupCopyExporter(router).ExportAsync(remote, job, Path.Combine(directory, "downloaded-copy.zip"));
        Assert.Equal(await File.ReadAllBytesAsync(latestLocal.Backup.Location), await File.ReadAllBytesAsync(exported.Path));
        Assert.Equal(beforeAudit, System.Text.Json.JsonSerializer.Serialize(await catalog.ReadAsync()));
        Assert.Empty(Directory.GetFiles(directory, ".onec-export-*.partial"));
        Assert.Equal(2, Directory.GetFiles(localDirectory).Length);
        Assert.Single(Directory.GetFiles(Path.Combine(RemoteRoot, remoteDirectory.TrimStart('/'))));
        foreach (var record in records) await new BackupCopyVerifier(router).VerifyAsync(record, job);
        Assert.Empty(Directory.GetFiles(Path.Combine(temporary, ".onec-backup-ftp-verification")));
        Assert.False(Directory.Exists(Path.Combine(directory, "verify")));
        Assert.False(Directory.Exists(Path.Combine(directory, "work")));
    }

    private sealed class FixtureCredentials : IFtpCredentialStore
    {
        public Task<NetworkCredential> GetAsync(string key, CancellationToken token)
        {
            Assert.Equal("local-fixture", key);
            return Task.FromResult(new NetworkCredential("backup-test", "local-fixture-password"));
        }
    }

    [FtpFact]
    public async Task ReadinessProbeChecksFtpAndLocalWithoutKeepingFilesOrChangingExistingBackups()
    {
        var temporary = Path.Combine(directory, "HDD probe");
        var local = Path.Combine(directory, "local probe");
        var job = Job with { TemporaryDirectory = temporary,
            AdditionalDestinations = [new BackupDestination { DestinationDirectory = local }] };
        var remote = Path.Combine(RemoteRoot, remoteDirectory.TrimStart('/'));
        Directory.CreateDirectory(remote);
        Directory.CreateDirectory(local);
        await File.WriteAllTextAsync(Path.Combine(remote, "unrelated.bak"), "remote backup marker");
        await File.WriteAllTextAsync(Path.Combine(local, "unrelated.bak"), "local backup marker");
        var router = new RoutedBackupStorage(new FixtureCredentials(), Path.Combine(directory, "default verify"));
        var report = await new StorageReadinessChecker(router, Path.Combine(directory, "default work")).CheckAsync(job);
        Assert.Null(report.TemporaryError);
        Assert.Equal(2, report.Destinations.Count);
        Assert.All(report.Destinations, result => Assert.True(result.Succeeded, result.Error));
        Assert.Equal("remote backup marker", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(remote))));
        Assert.Equal("local backup marker", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(local))));
        Assert.Empty(Directory.GetFiles(temporary, "*", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(directory, "default work")));
        Assert.False(Directory.Exists(Path.Combine(directory, "default verify")));
    }
    private sealed class FixtureClock : TimeProvider
    { public override DateTimeOffset GetUtcNow() => new(2026, 10, 11, 13, 0, 0, TimeSpan.Zero); }

    [FtpTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadedCopyIsExactAndSourceIsPreserved(bool archive)
    {
        var source = Path.Combine(directory, "Бухгалтерия.bak");
        var contents = new byte[200000];
        new Random(75).NextBytes(contents);
        await File.WriteAllBytesAsync(source, contents);
        var progress = new List<BackupProgressSnapshot>();
        using var progressScope = BackupProgressContext.Begin(Job, Guid.NewGuid(), progress.Add);
        var stored = await new BackupDelivery(Storage()).DeliverAsync(Job with { ArchiveCopies = archive }, source,
            Path.Combine(directory, "stage"));
        var target = Path.Combine(RemoteRoot, remoteDirectory.TrimStart('/'), "Бухгалтерия.bak" + (archive ? ".zip" : ""));
        Assert.Equal(new FileInfo(target).Length, stored.Length);
        Assert.Contains(progress, e => e.Progress.Stage == BackupStage.CopyingToStorage && e.Progress.Total == stored.Length && e.Progress.Completed == stored.Length);
        Assert.Equal(BackupStage.Verifying, progress[^1].Progress.Stage);
        if (archive)
        {
            using var zip = ZipFile.OpenRead(target);
            using var restored = new MemoryStream();
            await using (var entry = Assert.Single(zip.Entries).Open()) await entry.CopyToAsync(restored);
            Assert.Equal(contents, restored.ToArray());
            Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(directory, "stage")));
        }
        else Assert.Equal(contents, await File.ReadAllBytesAsync(target));
        Assert.Equal(contents, await File.ReadAllBytesAsync(source));
        Assert.StartsWith("ftp://127.0.0.1:", stored.Location);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target)!, "*.tmp"));
        Assert.Empty(Directory.GetFiles(Path.Combine(directory, "verify")));
    }

    [FtpFact]
    public async Task ExistingFtpCopyIsNotOverwritten()
    {
        var source = Path.Combine(directory, "backup.bak");
        await File.WriteAllTextAsync(source, "first copy");
        await Storage().StoreAsync(source, Job);
        await File.WriteAllTextAsync(source, "different copy");
        await Assert.ThrowsAsync<IOException>(() => Storage().StoreAsync(source, Job));
        var target = Path.Combine(RemoteRoot, remoteDirectory.TrimStart('/'), "backup.bak");
        Assert.Equal("first copy", await File.ReadAllTextAsync(target));
    }

    [FtpFact]
    public async Task ServerCorruptionIsDetectedAndTemporaryUploadIsRemoved()
    {
        var source = Path.Combine(directory, "corrupt.bak");
        await File.WriteAllTextAsync(source, "original backup");
        await Assert.ThrowsAsync<InvalidDataException>(() => Storage().StoreAsync(source, Job));
        Assert.Empty(Directory.GetFiles(Path.Combine(RemoteRoot, remoteDirectory.TrimStart('/'))));
        Assert.Empty(Directory.GetFiles(Path.Combine(directory, "verify")));
        Assert.Equal("original backup", await File.ReadAllTextAsync(source));
    }

    [FtpFact]
    public async Task InvalidCredentialsDoNotProduceSuccessfulCopy()
    {
        var source = Path.Combine(directory, "backup.bak");
        await File.WriteAllTextAsync(source, "original backup");
        await Assert.ThrowsAnyAsync<Exception>(() => Storage("incorrect-fixture-password").StoreAsync(source, Job));
        Assert.False(Directory.Exists(Path.Combine(RemoteRoot, remoteDirectory.TrimStart('/'))));
        Assert.Equal("original backup", await File.ReadAllTextAsync(source));
    }

    [FtpFact]
    public async Task RequiredTlsDoesNotFallBackToPlainFtp()
    {
        var source = Path.Combine(directory, "backup.bak");
        await File.WriteAllTextAsync(source, "original backup");
        await Assert.ThrowsAnyAsync<Exception>(() => Storage().StoreAsync(source, Job with { Ftp = Job.Ftp! with { UseTls = true } }));
        Assert.False(Directory.Exists(Path.Combine(RemoteRoot, remoteDirectory.TrimStart('/'))));
    }

    [FtpFact]
    public async Task RetentionRemovesOnlyRegisteredOldFtpCopies()
    {
        var job = Job with { KeepLast = 1 };
        var catalog = new BackupCatalog(Path.Combine(directory, "catalog.json"));
        var completion = new BackupCompletion(Storage(), catalog);
        var firstSource = Path.Combine(directory, "first.bak");
        await File.WriteAllTextAsync(firstSource, "first backup");
        var first = await completion.CompleteAsync(job, firstSource, directory, DateTimeOffset.UtcNow);
        var remotePath = Path.Combine(RemoteRoot, remoteDirectory.TrimStart('/'));
        await File.WriteAllTextAsync(Path.Combine(remotePath, "unregistered.bak"), "keep me");
        var secondSource = Path.Combine(directory, "second.bak");
        await File.WriteAllTextAsync(secondSource, "second backup");
        var second = await completion.CompleteAsync(job, secondSource, directory, DateTimeOffset.UtcNow);
        Assert.Contains(first.Record.Id, second.Retention.Deleted);
        Assert.Empty(second.Retention.Failures);
        Assert.False(File.Exists(Path.Combine(remotePath, "first.bak")));
        Assert.True(File.Exists(Path.Combine(remotePath, "second.bak")));
        Assert.True(File.Exists(Path.Combine(remotePath, "unregistered.bak")));
        Assert.Equal(second.Record, Assert.Single(await catalog.ReadAsync()));
        Assert.True(File.Exists(firstSource));
    }

    [FtpFact]
    public async Task ModifiedFtpCopyIsNotDeleted()
    {
        var source = Path.Combine(directory, "backup.bak");
        await File.WriteAllTextAsync(source, "original backup");
        var stored = await Storage().StoreAsync(source, Job);
        var remotePath = Path.Combine(RemoteRoot, remoteDirectory.TrimStart('/'), "backup.bak");
        await File.WriteAllTextAsync(remotePath, "modified backup");
        await Assert.ThrowsAsync<InvalidDataException>(() => Storage().DeleteAsync(stored, Job));
        Assert.Equal("modified backup", await File.ReadAllTextAsync(remotePath));
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
