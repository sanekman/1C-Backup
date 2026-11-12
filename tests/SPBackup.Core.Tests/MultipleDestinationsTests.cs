using System.Net;
using System.Text.Json;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class MultipleDestinationsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OneCBackup-multiple-" + Guid.NewGuid());
    private readonly Guid jobId = Guid.NewGuid();
    private BackupJob Job => new() { Id = jobId, Name = "Two destinations", Source = "database",
        DestinationDirectory = Path.Combine(root, "first"), KeepLast = 1,
        AdditionalDestinations = [new BackupDestination { DestinationDirectory = Path.Combine(root, "second") }] };
    private BackupCatalog Catalog => new(Path.Combine(root, "catalog.json"));
    private BackupRunHistory History => new(Path.Combine(root, "runs.json"));
    public MultipleDestinationsTests() => Directory.CreateDirectory(root);

    private LocalBackupRunner Runner(IBackupExecutor executor, IBackupStorage storage, Action<BackupProgressSnapshot>? progress = null) =>
        new(executor, new BackupCompletion(storage, Catalog, new Passwords()), History, Path.Combine(root, "work"), progress: progress);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreatesOneDumpAndOneArchiveWithIdenticalBytesForBothDestinations(bool encrypted)
    {
        var executor = new Executor();
        var storage = new Storage();
        var job = Job with { ArchiveCopies = true, ArchivePasswordKey = encrypted ? "test-password" : null };
        var progress = new List<BackupProgressSnapshot>();
        var run = await Runner(executor, storage, progress.Add).RunAsync(job);
        Assert.Equal(run.RunId, progress[^1].RunId);
        Assert.Equal(BackupStage.Completed, progress[^1].Progress.Stage);
        Assert.Equal(2, progress[^1].Destinations.Count);
        Assert.All(progress[^1].Destinations, d => Assert.Equal(100, d.Progress.Percent));
        Assert.Equal(BackupRunStatus.Succeeded, run.Status);
        Assert.Equal(1, executor.Count);
        Assert.Equal(2, storage.Paths.Count);
        Assert.Single(storage.Paths.Distinct()); // The archive is prepared once, not once per destination.
        var records = await Catalog.ReadAsync();
        Assert.Equal(2, records.Count);
        Assert.Equal(records[0].Backup.Sha256, records[1].Backup.Sha256);
        Assert.Equal(await File.ReadAllBytesAsync(records[0].Backup.Location), await File.ReadAllBytesAsync(records[1].Backup.Location));
        foreach (var record in records)
        {
            await new BackupCopyVerifier(new FileBackupStorage()).VerifyAsync(record, job);
            if (encrypted)
            {
                using var zip = new ICSharpCode.SharpZipLib.Zip.ZipFile(record.Backup.Location) { Password = "Password-2026" };
                Assert.Equal(256, zip[0].AESKeySize);
                using var reader = new StreamReader(zip.GetInputStream(0));
                Assert.Equal("fixture backup", await reader.ReadToEndAsync());
            }
        }
        Assert.False(Directory.Exists(run.WorkDirectory));
        Assert.Equal(2, Assert.Single(await History.ReadAsync()).Destinations!.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailureOfEitherDestinationDoesNotBlockTheOtherAndIsDurablyPartial(bool failPrimary)
    {
        var storage = new Storage { FailedDirectory = failPrimary ? Job.DestinationDirectory : Job.AdditionalDestinations![0].DestinationDirectory };
        var run = await Runner(new Executor(), storage).RunAsync(Job);
        Assert.Equal(BackupRunStatus.PartiallySucceeded, run.Status);
        Assert.Equal(2, storage.Paths.Count);
        Assert.Single(run.Destinations!, result => result.Record is not null);
        Assert.Single(run.Destinations!, result => result.Error is not null);
        var record = Assert.Single(await Catalog.ReadAsync());
        Assert.Equal(record.Backup, run.Backup);
        Assert.True(Directory.Exists(run.WorkDirectory));
        Assert.Contains("unavailable destination", run.Error);
        var saved = Assert.Single(await History.ReadAsync());
        Assert.Equal(BackupRunStatus.PartiallySucceeded, saved.Status);
        await new BackupCopyVerifier(new FileBackupStorage()).VerifyAsync(record, Job);
    }

    [Fact]
    public async Task AllFailedDestinationsAreReportedWithoutAFalseSuccess()
    {
        var run = await Runner(new Executor(), new Storage { FailAll = true }).RunAsync(Job);
        Assert.Equal(BackupRunStatus.Failed, run.Status);
        Assert.Null(run.Backup);
        Assert.Equal(2, run.Destinations!.Count);
        Assert.All(run.Destinations, result => Assert.NotNull(result.Error));
        Assert.Empty(await Catalog.ReadAsync());
    }

    [Fact]
    public async Task MissingFtpCredentialDoesNotPreventLocalCopy()
    {
        var ftp = new BackupDestination { StorageKind = StorageKind.Ftp, DestinationDirectory = "/backups",
            Ftp = new FtpSettings { Host = "localhost", CredentialKey = "missing" } };
        var job = Job with { AdditionalDestinations = [ftp] };
        var executor = new Executor();
        var storage = new RoutedBackupStorage(new MissingCredentials(), Path.Combine(root, "verify"));
        var run = await Runner(executor, storage).RunAsync(job);
        Assert.Equal(1, executor.Count);
        Assert.Equal(BackupRunStatus.PartiallySucceeded, run.Status);
        Assert.True(File.Exists(Assert.Single(await Catalog.ReadAsync()).Backup.Location));
        Assert.Contains("missing credential", run.Error);
    }

    [Fact]
    public async Task RetentionRunsPerDestinationAndNeverPrunesAFailedDestination()
    {
        var storage = new Storage();
        var runner = Runner(new Executor(), storage);
        await runner.RunAsync(Job);
        var oldSecondary = (await Catalog.ReadAsync()).Single(record => record.Backup.Location.StartsWith(Job.AdditionalDestinations![0].DestinationDirectory));
        var unrelated = Path.Combine(Job.AdditionalDestinations![0].DestinationDirectory, "unregistered.bak");
        await File.WriteAllTextAsync(unrelated, "keep this");
        storage.FailedDirectory = Job.AdditionalDestinations![0].DestinationDirectory;
        var partial = await runner.RunAsync(Job);
        Assert.Equal(BackupRunStatus.PartiallySucceeded, partial.Status);
        Assert.Equal(2, (await Catalog.ReadAsync()).Count);
        Assert.Contains(oldSecondary, await Catalog.ReadAsync());
        Assert.True(File.Exists(oldSecondary.Backup.Location));
        storage.FailedDirectory = null;
        var success = await runner.RunAsync(Job);
        Assert.Equal(BackupRunStatus.Succeeded, success.Status);
        Assert.Equal(2, (await Catalog.ReadAsync()).Count);
        Assert.DoesNotContain(oldSecondary, await Catalog.ReadAsync());
        Assert.False(File.Exists(oldSecondary.Backup.Location));
        Assert.Equal("keep this", await File.ReadAllTextAsync(unrelated));
    }

    [Fact]
    public async Task CancellationAfterDeliveryKeepsEveryCompletedRecord()
    {
        using var cancellation = new CancellationTokenSource();
        var storage = new Storage { AfterStore = () => cancellation.Cancel() };
        var run = await Runner(new Executor(), storage).RunAsync(Job, cancellation.Token);
        Assert.Equal(BackupRunStatus.Cancelled, run.Status);
        var records = await Catalog.ReadAsync();
        Assert.NotEmpty(records);
        Assert.All(records, record => Assert.True(File.Exists(record.Backup.Location)));
        Assert.Contains(records, record => record.Backup == run.Backup);
        Assert.Equal(records.Count, run.Destinations!.Count(result => result.Record is not null));
        Assert.Equal(2, run.Destinations!.Count);
        Assert.All(run.Destinations, result => Assert.True(result.Record is not null || result.Error is not null));
        Assert.Equal(BackupRunStatus.Cancelled, Assert.Single(await History.ReadAsync()).Status);
    }

    [Fact]
    public async Task DifferentCountsAndInheritedCountsPruneIndependentlyOnlyAfterSuccessfulDelivery()
    {
        var second = Job.AdditionalDestinations![0] with { KeepLast = 4 };
        var third = new BackupDestination { DestinationDirectory = Path.Combine(root, "third") };
        var job = Job with { KeepLast = 2, AdditionalDestinations = [second, third] };
        var storage = new Storage();
        var runner = Runner(new Executor(), storage);
        for (var index = 0; index < 5; index++)
            Assert.Equal(BackupRunStatus.Succeeded, (await runner.RunAsync(job)).Status);
        var records = await Catalog.ReadAsync();
        Assert.Equal(8, records.Count);
        Assert.Equal(2, Directory.GetFiles(job.DestinationDirectory).Length);
        Assert.Equal(4, Directory.GetFiles(second.DestinationDirectory).Length);
        Assert.Equal(2, Directory.GetFiles(third.DestinationDirectory).Length);
        var keptSecondary = Directory.GetFiles(second.DestinationDirectory).Order().ToArray();
        var unrelated = Path.Combine(second.DestinationDirectory, "unregistered.bak");
        await File.WriteAllTextAsync(unrelated, "keep this");

        job = job with { KeepLast = 1, AdditionalDestinations = [second with { KeepLast = 3 }, third] };
        storage.FailedDirectory = second.DestinationDirectory;
        Assert.Equal(BackupRunStatus.PartiallySucceeded, (await runner.RunAsync(job)).Status);
        Assert.Single(Directory.GetFiles(job.DestinationDirectory));
        Assert.Single(Directory.GetFiles(third.DestinationDirectory)); // Inherited count follows the job.
        Assert.All(keptSecondary, file => Assert.True(File.Exists(file))); // Lower limit cannot prune failed delivery.
        Assert.Equal(6, (await Catalog.ReadAsync()).Count);

        storage.FailedDirectory = null;
        Assert.Equal(BackupRunStatus.Succeeded, (await runner.RunAsync(job)).Status);
        Assert.Equal(5, (await Catalog.ReadAsync()).Count);
        Assert.Single(Directory.GetFiles(job.DestinationDirectory));
        Assert.Single(Directory.GetFiles(third.DestinationDirectory));
        Assert.Equal(4, Directory.GetFiles(second.DestinationDirectory).Length); // Three backups and the unrelated file.
        Assert.Equal("keep this", await File.ReadAllTextAsync(unrelated));
        foreach (var record in await Catalog.ReadAsync())
            await new BackupCopyVerifier(new FileBackupStorage()).VerifyAsync(record, job);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidDestinationCountCannotStartBackupOrDeleteExistingFiles(int count)
    {
        var storage = new Storage();
        var executor = new Executor();
        var runner = Runner(executor, storage);
        Assert.Equal(BackupRunStatus.Succeeded, (await runner.RunAsync(Job)).Status);
        var existing = await Catalog.ReadAsync();
        var invalid = Job with { AdditionalDestinations = [Job.AdditionalDestinations![0] with { KeepLast = count }] };
        await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(invalid));
        Assert.Equal(1, executor.Count);
        Assert.Equal(existing, await Catalog.ReadAsync());
        Assert.All(existing, record => Assert.True(File.Exists(record.Backup.Location)));
    }

    [Fact]
    public async Task PerDestinationCountRoundTripsAndOmittedCountKeepsLegacyInheritance()
    {
        var store = new JobStore(Path.Combine(root, "jobs.json"));
        await store.SaveAsync([Job]);
        var legacy = Assert.Single(await store.LoadAsync());
        Assert.Null(legacy.AdditionalDestinations![0].KeepLast);
        Assert.All(legacy.StorageJobs, target => Assert.Equal(legacy.KeepLast, target.KeepLast));
        var job = Job with { KeepLast = 7, AdditionalDestinations = [Job.AdditionalDestinations![0] with { KeepLast = 30 }] };
        await store.SaveAsync([job]);
        var restored = Assert.Single(await store.LoadAsync());
        Assert.Equal(new[] { 7, 30 }, restored.StorageJobs.Select(target => target.KeepLast));
        Assert.Empty(restored.Validate());
    }

    [Fact]
    public async Task LocalSecondaryOfFtpPrimaryCanBeVerifiedAndExtracted()
    {
        var source = Path.Combine(root, "base");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "1Cv8.1CD"), "1C fixture bytes");
        var job = Job with { Source = source, OneCMethod = OneCBackupMethod.FileArchive,
            StorageKind = StorageKind.Ftp, DestinationDirectory = "/backups",
            Ftp = new FtpSettings { Host = "localhost", CredentialKey = "missing" } };
        var storage = new RoutedBackupStorage(new MissingCredentials(), Path.Combine(root, "verify"));
        var run = await Runner(new OneCFileArchiveExecutor(), storage).RunAsync(job);
        Assert.Equal(BackupRunStatus.PartiallySucceeded, run.Status);
        var record = Assert.Single(await Catalog.ReadAsync());
        await new BackupCopyVerifier(storage).VerifyAsync(record, job);
        var extracted = await new OneCArchiveExtractor().ExtractAsync(record, job, root);
        Assert.Equal("1C fixture bytes", await File.ReadAllTextAsync(extracted.Path));
        await Assert.ThrowsAsync<InvalidDataException>(() => new BackupCopyVerifier(storage).VerifyAsync(record, job with { Id = Guid.NewGuid() }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new BackupCopyVerifier(storage).VerifyAsync(record, job with { AdditionalDestinations = null }));
    }

    [Fact]
    public void LegacyJobsKeepTheirPrimaryDestinationAndNewSettingsRoundTrip()
    {
        var original = Job with { AdditionalDestinations = null };
        var legacyJson = JsonSerializer.Serialize(original);
        Assert.DoesNotContain("AdditionalDestinations", legacyJson);
        Assert.Single(JsonSerializer.Deserialize<BackupJob>(legacyJson)!.StorageJobs);
        var restored = JsonSerializer.Deserialize<BackupJob>(JsonSerializer.Serialize(Job))!;
        Assert.Equal(Job.AdditionalDestinations![0], Assert.Single(restored.AdditionalDestinations!));
        Assert.Empty(restored.Validate());
    }

    [Fact]
    public void SwitchingTlsDoesNotAllowTheSameFtpFolderTwice()
    {
        var job = Job with { StorageKind = StorageKind.Ftp, DestinationDirectory = "/backups/",
            Ftp = new FtpSettings { Host = "EXAMPLE.COM", CredentialKey = "first", UseTls = true },
            AdditionalDestinations = [new BackupDestination { StorageKind = StorageKind.Ftp, DestinationDirectory = "/backups",
                Ftp = new FtpSettings { Host = "example.com", CredentialKey = "second", UseTls = false } }] };
        Assert.Contains(job.Validate(), error => error.Contains("несколько раз"));
        Assert.Empty((job with { AdditionalDestinations = [job.AdditionalDestinations![0] with { DestinationDirectory = "/Backups" }] }).Validate());
    }

    [Fact]
    public void DuplicateInvalidAndExcessiveDestinationsAreRejected()
    {
        Assert.Contains((Job with { AdditionalDestinations = [new BackupDestination { DestinationDirectory = Job.DestinationDirectory + Path.DirectorySeparatorChar }] }).Validate(), error => error.Contains("несколько раз"));
        Assert.Contains((Job with { AdditionalDestinations = [new BackupDestination { DestinationDirectory = "relative-folder" }] }).Validate(), error => error.Contains("полный путь"));
        Assert.Contains((Job with { AdditionalDestinations = Enumerable.Range(0, 8).Select(index => new BackupDestination { DestinationDirectory = Path.Combine(root, "extra-" + index) }).ToArray() }).Validate(), error => error.Contains("не более 8"));
        Assert.NotEmpty((Job with { AdditionalDestinations = [null!] }).Validate());
    }

    private sealed class Executor : IBackupExecutor
    {
        public int Count { get; private set; }
        public async Task<string> CreateAsync(BackupJob job, string workingDirectory, CancellationToken cancellationToken = default)
        {
            Count++;
            var path = Path.Combine(workingDirectory, "database-" + Count + ".bak");
            await File.WriteAllTextAsync(path, "fixture backup", cancellationToken);
            return path;
        }
    }

    private sealed class Passwords : IArchivePasswordStore
    {
        public Task<string> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult("Password-2026");
    }

    private sealed class MissingCredentials : IFtpCredentialStore
    {
        public Task<NetworkCredential> GetAsync(string key, CancellationToken cancellationToken = default) => throw new InvalidDataException("missing credential");
    }

    private sealed class Storage : IBackupStorage
    {
        private readonly FileBackupStorage files = new();
        public string? FailedDirectory { get; set; }
        public bool FailAll { get; set; }
        public Action? AfterStore { get; init; }
        public System.Collections.Concurrent.ConcurrentBag<string> Paths { get; } = [];
        public async Task<StoredBackup> StoreAsync(string path, BackupJob job, CancellationToken cancellationToken = default)
        {
            Paths.Add(path);
            if (FailAll || job.DestinationDirectory == FailedDirectory) throw new IOException("unavailable destination");
            var result = await files.StoreAsync(path, job, cancellationToken);
            AfterStore?.Invoke();
            return result;
        }
        public Task VerifyAsync(StoredBackup backup, BackupJob job, CancellationToken cancellationToken = default) => files.VerifyAsync(backup, job, cancellationToken);
        public Task DeleteAsync(StoredBackup backup, BackupJob job, CancellationToken cancellationToken = default) => files.DeleteAsync(backup, job, cancellationToken);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
