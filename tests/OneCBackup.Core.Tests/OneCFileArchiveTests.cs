using System.IO.Compression;
using System.Text;
using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class OneCFileArchiveTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OneCBackup-filearchive-" + Guid.NewGuid());
    private readonly Guid jobId = Guid.NewGuid();
    private string SourceFile => Path.Combine(root, "source", "1Cv8.1CD");
    private BackupJob Job => new()
    {
        Id = jobId, Name = "Archive file database", Source = Path.GetDirectoryName(SourceFile)!,
        DestinationDirectory = Path.Combine(root, "copies"), OneCMethod = OneCBackupMethod.FileArchive
    };

    public OneCFileArchiveTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SourceFile)!);
        File.WriteAllBytes(SourceFile, Encoding.UTF8.GetBytes("closed database fixture"));
    }

    private LocalBackupRunner Runner => new(new OneCBackupExecutor(new ForbiddenProcess()),
        new BackupCompletion(new FileBackupStorage(), new BackupCatalog(Path.Combine(root, "catalog.json"))),
        new BackupRunHistory(Path.Combine(root, "history.json")), Path.Combine(root, "work"));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CreatesSingleZipWithoutPlatformAndPreservesSource(bool additionalZip, bool directFile)
    {
        var before = await File.ReadAllBytesAsync(SourceFile);
        var job = Job with { ArchiveCopies = additionalZip, Source = directFile ? SourceFile : Job.Source };
        var result = await Runner.RunAsync(job);
        Assert.Equal(BackupRunStatus.Succeeded, result.Status);
        Assert.EndsWith(".1CD.zip", result.Backup!.Location);
        Assert.DoesNotContain(".zip.zip", result.Backup.Location);
        using var zip = ZipFile.OpenRead(result.Backup.Location);
        var entry = Assert.Single(zip.Entries);
        Assert.Equal("1Cv8.1CD", entry.FullName);
        using var output = new MemoryStream();
        await entry.Open().CopyToAsync(output);
        Assert.Equal(before, output.ToArray());
        Assert.Equal(before, await File.ReadAllBytesAsync(SourceFile));
        Assert.False(Directory.Exists(result.WorkDirectory));
        Assert.Single(await new BackupCatalog(Path.Combine(root, "catalog.json")).ReadAsync());
    }

    [Fact]
    public async Task BusyDatabaseFailsWithoutPruningPreviousCopyAndLockIsReleasedAfterFailure()
    {
        var runner = Runner;
        var first = await runner.RunAsync(Job with { KeepLast = 1 });
        Assert.Equal(BackupRunStatus.Succeeded, first.Status);
        await using (var activeSession = new FileStream(SourceFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var failed = await runner.RunAsync(Job with { KeepLast = 1 });
            Assert.Equal(BackupRunStatus.Failed, failed.Status);
            Assert.Contains("эксклюзивный доступ", failed.Error);
            Assert.True(File.Exists(first.Backup!.Location));
            Assert.Single(Directory.GetFiles(Job.DestinationDirectory));
            Assert.Empty(Directory.GetFiles(failed.WorkDirectory));
        }
        var next = await runner.RunAsync(Job with { KeepLast = 1 });
        Assert.Equal(BackupRunStatus.Succeeded, next.Status);
        Assert.False(File.Exists(first.Backup!.Location));
        Assert.Single(Directory.GetFiles(Job.DestinationDirectory));
    }

    [Fact]
    public async Task ExplicitOptInCopiesWhileWritableSessionIsOpenAndPersistsWarning()
    {
        var before = await File.ReadAllBytesAsync(SourceFile);
        await using var activeSession = new FileStream(SourceFile, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        if (OperatingSystem.IsWindows())
        {
            var exclusiveAttempt = await Runner.RunAsync(Job);
            Assert.Equal(BackupRunStatus.Failed, exclusiveAttempt.Status);
            Assert.Contains("эксклюзивный доступ", exclusiveAttempt.Error);
        }
        var result = await Runner.RunAsync(Job with { AllowOpenOneCDatabase = true });
        Assert.Equal(BackupRunStatus.Succeeded, result.Status);
        Assert.Contains(OneCFileArchiveExecutor.OpenDatabaseWarning, result.Warnings!);
        using var zip = ZipFile.OpenRead(result.Backup!.Location);
        using var output = new MemoryStream();
        await Assert.Single(zip.Entries).Open().CopyToAsync(output);
        Assert.Equal(before, output.ToArray());
        Assert.True(activeSession.CanWrite);
        var saved = Assert.Single(await new BackupRunHistory(Path.Combine(root, "history.json")).ReadAsync(), run => run.RunId == result.RunId);
        Assert.Contains(OneCFileArchiveExecutor.OpenDatabaseWarning, saved.Warnings!);
    }

    [Fact]
    public async Task OptInCannotOverrideAnExclusiveLockAndKeepsPreviousCopy()
    {
        var previous = await Runner.RunAsync(Job with { KeepLast = 1 });
        await using var held = new FileStream(SourceFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await Runner.RunAsync(Job with { AllowOpenOneCDatabase = true, KeepLast = 1 });
        Assert.Equal(BackupRunStatus.Failed, result.Status);
        Assert.Contains("совместным доступом", result.Error);
        Assert.True(File.Exists(previous.Backup!.Location));
        Assert.Empty(Directory.GetFiles(result.WorkDirectory));
    }

    [Fact]
    public async Task OpenDatabaseChoiceSurvivesSaveAndOldJobsStayExclusive()
    {
        var path = Path.Combine(root, "jobs-choice.json");
        var store = new JobStore(path);
        await store.SaveAsync([Job with { AllowOpenOneCDatabase = true }]);
        Assert.True(Assert.Single(await store.LoadAsync()).AllowOpenOneCDatabase);
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        json[0]!.AsObject().Remove("AllowOpenOneCDatabase");
        await File.WriteAllTextAsync(path, json.ToJsonString());
        Assert.False(Assert.Single(await store.LoadAsync()).AllowOpenOneCDatabase);
    }

    [Fact]
    public async Task EmptyOrMissingDatabaseDoesNotPublishCopy()
    {
        await File.WriteAllBytesAsync(SourceFile, []);
        var empty = await Runner.RunAsync(Job);
        Assert.Equal(BackupRunStatus.Failed, empty.Status);
        Assert.Contains("пуст", empty.Error);
        File.Delete(SourceFile);
        var missing = await Runner.RunAsync(Job);
        Assert.Equal(BackupRunStatus.Failed, missing.Status);
        Assert.False(Directory.Exists(Job.DestinationDirectory));
    }

    [Fact]
    public async Task StreamArchiveRetainsExclusiveHandleAndCancellationRemovesTemporaryZip()
    {
        await using var source = new FileStream(SourceFile, FileMode.Open, FileAccess.Read, FileShare.None);
        using var cancellation = new CancellationTokenSource();
        var destination = Path.Combine(root, "cancelled.zip");
        var progress = new ImmediateProgress(_ =>
        {
            // These handles cannot coexist on Windows, where the program executes.
            if (OperatingSystem.IsWindows())
                Assert.Throws<IOException>(() => new FileStream(SourceFile, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite));
            cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BackupArchiver().CreateZipAsync(
            source, "1Cv8.1CD", destination, progress, cancellation.Token));
        Assert.True(source.CanRead);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        source.Position = 0;
        await new BackupArchiver().CreateZipAsync(source, "1Cv8.1CD", destination);
        Assert.True(source.CanRead);
        Assert.True(File.Exists(destination));
    }

    [Fact]
    public async Task MethodIsPersistedAndLegacyJobsDefaultToPlatformDump()
    {
        var path = Path.Combine(root, "jobs.json");
        var store = new JobStore(path);
        await store.SaveAsync([Job]);
        Assert.Equal(OneCBackupMethod.FileArchive, Assert.Single(await store.LoadAsync()).OneCMethod);
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        json[0]!.AsObject().Remove("OneCMethod");
        await File.WriteAllTextAsync(path, json.ToJsonString());
        Assert.Equal(OneCBackupMethod.PlatformDump, Assert.Single(await store.LoadAsync()).OneCMethod);
    }

    [Fact]
    public void FileArchiveDoesNotRequireValidPlatformSettingsButUnknownMethodIsRejected()
    {
        Assert.Empty((Job with { OneC = new OneCSettings { ExecutablePath = "", TimeoutMinutes = 0 } }).Validate());
        Assert.NotEmpty((Job with { OneCMethod = (OneCBackupMethod)99 }).Validate());
    }

    private sealed class ForbiddenProcess : IBackupProcessRunner
    {
        public Task<int> RunAsync(BackupProcess command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The file archive method must never start the platform.");
    }

    private sealed class ImmediateProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
