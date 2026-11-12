using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class FileStorageRetryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sp-storage-retry-" + Guid.NewGuid().ToString("N"));
    private readonly Guid jobId = Guid.NewGuid();
    private BackupJob Job => new() { Id = jobId, Name = "Network retry", Source = "database", DestinationDirectory = Path.Combine(root, "copies") };
    private static IOException NetworkFailure() => new("Localized text must not control policy", unchecked((int)0x80070040));
    private static FileStorageOperations Immediate => new() { Delay = (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; } };
    public FileStorageRetryTests() => Directory.CreateDirectory(root);
    private async Task<string> Source()
    {
        var path = Path.Combine(root, "ready.zip"); var data = new byte[230000]; new Random(42).NextBytes(data);
        await File.WriteAllBytesAsync(path, data); return path;
    }

    [Fact]
    public async Task InterruptedWriteRestartsOnlyDeliveryAndVerifiesExactBytes()
    {
        var source = await Source(); var opens = 0; var delays = new List<int>(); var stages = new List<BackupStage>();
        var ops = Immediate with { OpenTemporary = path => ++opens == 1 ? new FaultedFile(path, NetworkFailure()) : Open(path),
            Delay = (retry, _) => { delays.Add(retry); return Task.CompletedTask; } };
        using var progress = BackupProgressContext.Begin(Job, Guid.NewGuid(), snapshot => stages.Add(snapshot.Progress.Stage));
        var stored = await new FileBackupStorage(_ => long.MaxValue, ops).StoreAsync(source, Job);
        Assert.Equal(2, opens); Assert.Equal(new[] { 1 }, delays);
        Assert.Contains(BackupStage.RetryingStorage, stages);
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(stored.Location));
        await new FileBackupStorage().VerifyAsync(stored, Job);
        Assert.Empty(Directory.GetFiles(Job.DestinationDirectory, "*.tmp"));
    }

    [Fact]
    public async Task PermanentOutageHasExactlyThreeAttemptsAndPreservesSource()
    {
        var source = await Source(); var opens = 0; var delays = new List<int>();
        var ops = Immediate with { OpenTemporary = path => { opens++; return new FaultedFile(path, NetworkFailure()); },
            Delay = (retry, _) => { delays.Add(retry); return Task.CompletedTask; } };
        var error = await Assert.ThrowsAsync<IOException>(() => new FileBackupStorage(_ => long.MaxValue, ops).StoreAsync(source, Job));
        Assert.Equal(3, opens); Assert.Equal(new[] { 1, 2 }, delays);
        Assert.Contains("пользователя службы", error.Message); Assert.Contains("64", error.Message);
        Assert.Equal(NetworkFailure().HResult, error.InnerException!.HResult);
        Assert.True(File.Exists(source)); Assert.Empty(Directory.GetFiles(Job.DestinationDirectory));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(39)]
    [InlineData(112)]
    [InlineData(1219)]
    [InlineData(1326)]
    [InlineData(1330)]
    public async Task PermissionsCredentialsAndCapacityAreNotRetried(int code)
    {
        var source = await Source(); var opens = 0; var original = new IOException("Untrusted localized text", unchecked((int)0x80070000) | code);
        var ops = Immediate with { OpenTemporary = path => { opens++; return new FaultedFile(path, original); },
            Delay = (_, _) => throw new InvalidOperationException("Permanent error must not retry") };
        var error = await Assert.ThrowsAsync<IOException>(() => new FileBackupStorage(_ => long.MaxValue, ops).StoreAsync(source, Job));
        Assert.Equal(1, opens); Assert.Same(original, error.InnerException);
        Assert.DoesNotContain(original.Message, error.Message);
        Assert.Contains(code.ToString(), error.Message); Assert.Empty(Directory.GetFiles(Job.DestinationDirectory));
    }

    [Fact]
    public async Task UnknownIoErrorKeepsItsOriginalTypeAndMessageWithoutRetry()
    {
        var source = await Source(); var error = new IOException("Unknown disk error");
        var ops = Immediate with { OpenTemporary = path => new FaultedFile(path, error),
            Delay = (_, _) => throw new InvalidOperationException("Unknown error must not retry") };
        Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => new FileBackupStorage(_ => long.MaxValue, ops).StoreAsync(source, Job)));
        Assert.Empty(Directory.GetFiles(Job.DestinationDirectory));
    }

    [Fact]
    public async Task CancellationDuringBackoffCleansItsPartialAndDoesNotRetry()
    {
        var source = await Source(); var opens = 0; using var stop = new CancellationTokenSource();
        var ops = Immediate with { OpenTemporary = path => { opens++; return new FaultedFile(path, NetworkFailure()); },
            Delay = async (_, token) => { stop.Cancel(); await Task.Delay(Timeout.Infinite, token); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FileBackupStorage(_ => long.MaxValue, ops).StoreAsync(source, Job, stop.Token));
        Assert.Equal(1, opens); Assert.Empty(Directory.GetFiles(Job.DestinationDirectory)); Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task LostRenameAcknowledgementAdoptsOnlyThisDeliveriesVerifiedArchive()
    {
        var source = await Source(); var opens = 0; var publications = 0;
        var ops = Immediate with { OpenTemporary = path => { opens++; return Open(path); },
            Publish = (temporary, target) => { publications++; File.Move(temporary, target, false); throw NetworkFailure(); } };
        var catalog = new BackupCatalog(Path.Combine(root, "copies.json"));
        var result = await new BackupCompletion(new FileBackupStorage(_ => long.MaxValue, ops), catalog)
            .CompleteAsync(Job, source, Path.Combine(root, "stage"), DateTimeOffset.UtcNow);
        Assert.Equal(1, opens); Assert.Equal(1, publications); Assert.Single(await catalog.ReadAsync());
        await new BackupCopyVerifier(new FileBackupStorage()).VerifyAsync(result.Record, Job);
        Assert.Single(Directory.GetFiles(Job.DestinationDirectory));
    }

    [Fact]
    public async Task ExistingIdenticalArchiveIsNeverAdoptedOrOverwritten()
    {
        var source = await Source(); Directory.CreateDirectory(Job.DestinationDirectory);
        var existing = Path.Combine(Job.DestinationDirectory, Path.GetFileName(source)); File.Copy(source, existing);
        var before = await File.ReadAllBytesAsync(existing);
        var ops = Immediate with { OpenTemporary = _ => throw new InvalidOperationException("Existing target"),
            Delay = (_, _) => throw new InvalidOperationException("Existing target") };
        await Assert.ThrowsAsync<IOException>(() => new FileBackupStorage(_ => long.MaxValue, ops).StoreAsync(source, Job));
        Assert.Equal(before, await File.ReadAllBytesAsync(existing));
    }

    [Fact]
    public async Task FileAppearingAfterFailedWriteCannotBeAdoptedWithoutPublicationAttempt()
    {
        var source = await Source(); var target = Path.Combine(Job.DestinationDirectory, Path.GetFileName(source));
        var ops = Immediate with { OpenTemporary = path => new FaultedFile(path, NetworkFailure()),
            Delay = (_, _) => { File.Copy(source, target); return Task.CompletedTask; } };
        var catalog = new BackupCatalog(Path.Combine(root, "copies.json"));
        await Assert.ThrowsAsync<IOException>(() => new BackupCompletion(new FileBackupStorage(_ => long.MaxValue, ops), catalog)
            .CompleteAsync(Job, source, Path.Combine(root, "stage"), DateTimeOffset.UtcNow));
        Assert.Empty(await catalog.ReadAsync()); Assert.True(File.Exists(target));
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task ModifiedFileAfterLostAcknowledgementIsNotRegisteredOrReplaced()
    {
        var source = await Source(); var target = Path.Combine(Job.DestinationDirectory, Path.GetFileName(source));
        var ops = Immediate with { Publish = (temporary, final) => { File.Move(temporary, final, false); File.WriteAllText(final, "changed"); throw NetworkFailure(); } };
        var catalog = new BackupCatalog(Path.Combine(root, "copies.json"));
        await Assert.ThrowsAsync<InvalidDataException>(() => new BackupCompletion(new FileBackupStorage(_ => long.MaxValue, ops), catalog)
            .CompleteAsync(Job, source, Path.Combine(root, "stage"), DateTimeOffset.UtcNow));
        Assert.Empty(await catalog.ReadAsync()); Assert.Equal("changed", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task CleanupFailureDoesNotMaskTheNetworkErrorAndIsRetriedForOwnedFiles()
    {
        var source = await Source(); var opens = 0; var deletes = 0;
        var log = new DiagnosticLog(root, DiagnosticComponent.Tests, "test", "test");
        using var scope = DiagnosticLog.Use(log);
        var ops = Immediate with { OpenTemporary = path => ++opens == 1 ? new FaultedFile(path, NetworkFailure()) : Open(path),
            DeleteTemporary = path => { if (++deletes == 1) throw NetworkFailure(); File.Delete(path); } };
        var stored = await new FileBackupStorage(_ => long.MaxValue, ops).StoreAsync(source, Job);
        await new FileBackupStorage().VerifyAsync(stored, Job);
        Assert.Empty(Directory.GetFiles(Job.DestinationDirectory, "*.tmp"));
        var entries = Directory.GetFiles(log.DirectoryPath!).SelectMany(File.ReadAllLines)
            .Select(line => JsonSerializer.Deserialize<DiagnosticEntry>(line, DiagnosticLog.Json)!).ToArray();
        Assert.Contains(entries, e => e.Event == DiagnosticEvent.StorageRetrying && e.Values!.Count == 1);
        Assert.Contains(entries, e => e.Event == DiagnosticEvent.StorageTemporaryCleanupFailed);
        Assert.All(Directory.GetFiles(log.DirectoryPath!), path => Assert.DoesNotContain(source, File.ReadAllText(path)));
    }

    [Fact]
    public async Task CorruptTransferIsNotRetriedOrPublished()
    {
        var source = await Source(); var opens = 0;
        var ops = Immediate with { OpenTemporary = path => { opens++; return new FaultedFile(path, null, corrupt: true); },
            Delay = (_, _) => throw new InvalidOperationException("Hash mismatch must not retry") };
        await Assert.ThrowsAsync<InvalidDataException>(() => new FileBackupStorage(_ => long.MaxValue, ops).StoreAsync(source, Job));
        Assert.Equal(1, opens); Assert.Empty(Directory.GetFiles(Job.DestinationDirectory));
    }

    [Fact]
    public async Task CleanupAccessFailureCannotReplaceTheTerminalNetworkError()
    {
        var source = await Source();
        var ops = Immediate with { OpenTemporary = path => new FaultedFile(path, NetworkFailure()),
            DeleteTemporary = _ => throw new UnauthorizedAccessException("Cleanup is temporarily blocked") };
        var error = await Assert.ThrowsAsync<IOException>(() => new FileBackupStorage(_ => long.MaxValue, ops).StoreAsync(source, Job));
        Assert.Equal(NetworkFailure().HResult, error.InnerException!.HResult);
        Assert.True(File.Exists(source)); Assert.False(File.Exists(Path.Combine(Job.DestinationDirectory, Path.GetFileName(source))));
        Assert.Equal(3, Directory.GetFiles(Job.DestinationDirectory, "*.tmp").Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetryingDeliveryDoesNotRepeatDatabaseCreationOrOtherDestinations(bool recovers)
    {
        var job = Job with { ArchiveCopies = true, AdditionalDestinations = [new() { DestinationDirectory = Path.Combine(root, "second") }] };
        var firstOpens = 0; var secondOpens = 0;
        var ops = Immediate with { OpenTemporary = path =>
        {
            if (Path.GetDirectoryName(path) == job.DestinationDirectory)
                return Interlocked.Increment(ref firstOpens) == 1 || !recovers ? new FaultedFile(path, NetworkFailure()) : Open(path);
            Interlocked.Increment(ref secondOpens); return Open(path);
        } };
        var executor = new Dump(); var catalog = new BackupCatalog(Path.Combine(root, "copies.json"));
        var history = new BackupRunHistory(Path.Combine(root, "runs.json"));
        var run = await new LocalBackupRunner(executor, new(new FileBackupStorage(_ => long.MaxValue, ops), catalog), history,
            Path.Combine(root, "work")).RunAsync(job);
        Assert.Equal(recovers ? BackupRunStatus.Succeeded : BackupRunStatus.PartiallySucceeded, run.Status); Assert.Equal(1, executor.Calls);
        Assert.Equal(recovers ? 2 : 3, firstOpens); Assert.Equal(1, secondOpens);
        Assert.Equal(!recovers, Directory.Exists(run.WorkDirectory));
        var records = await catalog.ReadAsync(); Assert.Equal(recovers ? 2 : 1, records.Count);
        Assert.Single(records.Select(r => r.Backup.Sha256).Distinct());
        foreach (var record in records)
        {
            using var zip = ZipFile.OpenRead(record.Backup.Location); using var input = Assert.Single(zip.Entries).Open();
            using var restored = new MemoryStream(); await input.CopyToAsync(restored); Assert.Equal(executor.Bytes, restored.ToArray());
        }
        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
        Assert.Equal(run.Status, Assert.Single(await history.ReadAsync()).Status);
    }

    [Fact]
    public async Task LongUnicodePathsSupportRetryIncompleteIncrementCacheRecoveryAndRestoration()
    {
        string LongDirectory(string category) => Enumerable.Range(0, 6).Aggregate(Path.Combine(root, category),
            (path, index) => Path.Combine(path, new string('д', 48) + index));
        var source = LongDirectory("источник"); var destination = LongDirectory("копии");
        var temporary = LongDirectory("рабочая папка"); var restoreParent = LongDirectory("восстановление");
        var relative = Path.Combine(new string('п', 48), new string('р', 48), new string('с', 48), new string('т', 48));
        var sourceFolder = Path.Combine(source, relative); Directory.CreateDirectory(sourceFolder); Directory.CreateDirectory(restoreParent);
        var good = Path.Combine(sourceFolder, "новые данные.txt"); var held = Path.Combine(sourceFolder, "занятый файл.txt");
        await File.WriteAllTextAsync(good, "первая версия"); await File.WriteAllTextAsync(held, "сохранённая доступная версия");
        Assert.True(good.Length > 260);
        var job = Job with { Kind = DatabaseKind.FilesAndFolders, Source = source, DestinationDirectory = destination,
            TemporaryDirectory = temporary, FileBackupMode = FileBackupMode.Incremental, FullBackupEvery = 7 };
        var opens = 0; var ops = Immediate with { OpenTemporary = path => ++opens == 1 ? new FaultedFile(path, NetworkFailure()) : Open(path) };
        var storage = new FileBackupStorage(_ => long.MaxValue, ops);
        var catalog = new BackupCatalog(Path.Combine(root, "copies.json")); var history = new BackupRunHistory(Path.Combine(root, "runs.json"));
        var profileWork = Path.Combine(root, "profile", "work");
        var runner = new LocalBackupRunner(new Dump(), new(storage, catalog), history, profileWork);
        var full = await runner.RunAsync(job); Assert.True(full.Status == BackupRunStatus.Succeeded, full.Error);
        Assert.True(full.Backup!.FileSnapshot!.IsFull); Assert.True(full.Backup.Location.Length > 260); Assert.Equal(2, opens);
        await File.WriteAllTextAsync(good, "вторая версия");
        BackupRun incomplete;
        await using (var file = new FileStream(held, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            incomplete = await runner.RunAsync(job);
        Assert.True(incomplete.Status == BackupRunStatus.Succeeded, incomplete.Error);
        Assert.NotEmpty(incomplete.Warnings!); Assert.False(incomplete.Backup!.FileSnapshot!.IsFull);
        File.Delete(Path.Combine(root, "profile", "file-snapshots", job.Id.ToString("N") + ".json"));
        await File.WriteAllTextAsync(good, "третья версия");
        var recovered = await runner.RunAsync(job); Assert.True(recovered.Status == BackupRunStatus.Succeeded, recovered.Error);
        Assert.Equal(incomplete.Backup.FileSnapshot.SnapshotId, recovered.Backup!.FileSnapshot!.ParentSnapshotId);
        Assert.Equal(2, recovered.Backup.FileSnapshot.Depth);
        var record = (await catalog.ReadAsync()).Single(r => r.Backup.Location == recovered.Backup.Location);
        var restored = await new FileBackupRestorer(storage, catalog, profileWork).RestoreAsync(record, job, restoreParent);
        var restoredGood = Path.Combine(restored.Directory, relative, Path.GetFileName(good)); Assert.True(restoredGood.Length > 260);
        Assert.Equal("третья версия", await File.ReadAllTextAsync(restoredGood));
        Assert.Equal("сохранённая доступная версия", await File.ReadAllTextAsync(Path.Combine(restored.Directory, relative, Path.GetFileName(held))));
        Assert.Empty(Directory.GetFiles(destination, "*.tmp"));
    }

    private static FileStream Open(string path) => new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
    private sealed class FaultedFile(string path, Exception? error, bool corrupt = false)
        : FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous)
    {
        private bool first = true;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
        {
            await base.WriteAsync(bytes, token);
            if (!first) return; first = false;
            if (corrupt && bytes.Length > 0) { var position = Position; Position -= bytes.Length; WriteByte((byte)(bytes.Span[0] ^ 0xff)); Position = position; }
            if (error is not null) throw error;
        }
    }
    private sealed class Dump : IBackupExecutor
    {
        internal int Calls;
        internal readonly byte[] Bytes = RandomNumberGenerator.GetBytes(230000);
        public async Task<string> CreateAsync(BackupJob job, string work, CancellationToken token)
        { Calls++; var path = Path.Combine(work, "fixture.dt"); await File.WriteAllBytesAsync(path, Bytes, token); return path; }
    }
    public void Dispose() => Directory.Delete(root, true);
}
