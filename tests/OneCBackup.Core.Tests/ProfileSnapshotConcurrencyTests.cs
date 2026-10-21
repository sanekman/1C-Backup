using System.Text.Json;
using System.Text.Json.Serialization;
using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class ProfileSnapshotConcurrencyTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OneCBackup-snapshot-" + Guid.NewGuid());
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };
    public ProfileSnapshotConcurrencyTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task TerminalHistoryCanBeSavedWhileDesktopReadsRunningSnapshot()
    {
        var path = Path.Combine(directory, "runs.json");
        var history = new BackupRunHistory(path);
        var running = new BackupRun(Guid.NewGuid(), Guid.NewGuid(), "service fixture", DateTimeOffset.UtcNow,
            null, BackupRunStatus.Running, Path.Combine(directory, "work"));
        await history.SaveAsync(running);
        await using var desktopReader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var finished = running with { Status = BackupRunStatus.Succeeded, FinishedUtc = DateTimeOffset.UtcNow };

        await history.SaveAsync(finished);

        Assert.Equal(finished, Assert.Single(await new BackupRunHistory(path).ReadAsync()));
        var previous = await JsonSerializer.DeserializeAsync<List<BackupRun>>(desktopReader, Options);
        Assert.Equal(running, Assert.Single(previous!));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task SavingJobDoesNotInvalidateReadersOfPreviousSettings()
    {
        var path = Path.Combine(directory, "jobs.json");
        var store = new JobStore(path);
        var job = new BackupJob { Name = "before", Source = "fixture", DestinationDirectory = directory };
        await store.SaveAsync([job]);
        await using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var updated = job with { Name = "after", Enabled = false };

        await store.SaveAsync([updated]);

        Assert.Equal(updated, Assert.Single(await new JobStore(path).LoadAsync()));
        Assert.Equal(job, Assert.Single((await JsonSerializer.DeserializeAsync<List<BackupJob>>(reader, Options))!));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task CancelledHistorySavePreservesPublishedSnapshot()
    {
        var path = Path.Combine(directory, "runs.json");
        var history = new BackupRunHistory(path);
        var running = new BackupRun(Guid.NewGuid(), Guid.NewGuid(), "fixture", DateTimeOffset.UtcNow,
            null, BackupRunStatus.Running, Path.Combine(directory, "work"));
        await history.SaveAsync(running);
        var original = await File.ReadAllBytesAsync(path);
        using var stop = new CancellationTokenSource(); stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => history.SaveAsync(
            running with { Status = BackupRunStatus.Cancelled, FinishedUtc = DateTimeOffset.UtcNow }, stop.Token));

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task IndependentReaderCanContinuouslyOpenHistoryWhileServicePublishesUpdates()
    {
        var path = Path.Combine(directory, "runs.json");
        var writer = new BackupRunHistory(path);
        var reader = new BackupRunHistory(path);
        var run = new BackupRun(Guid.NewGuid(), Guid.NewGuid(), "fixture", DateTimeOffset.UtcNow,
            null, BackupRunStatus.Running, Path.Combine(directory, "work"));
        await writer.SaveAsync(run);
        // A separate instance models the desktop process; no shared semaphore.
        var reads = Task.Run(async () =>
        {
            for (var i = 0; i < 250; i++)
            {
                var observed = Assert.Single(await reader.ReadAsync());
                Assert.Equal(run.RunId, observed.RunId);
                Assert.True(observed.JobName == "fixture" || observed.JobName.StartsWith("revision-", StringComparison.Ordinal));
            }
        });
        var writes = Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++) await writer.SaveAsync(run with { JobName = "revision-" + i });
        });
        await Task.WhenAll(reads, writes).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("revision-99", Assert.Single(await reader.ReadAsync()).JobName);
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task UnreadableHistoryPathIsReportedInsteadOfReturningEmptyHistory()
    {
        var path = Path.Combine(directory, "runs.json");
        Directory.CreateDirectory(path);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new BackupRunHistory(path).ReadAsync());
        Assert.True(Directory.Exists(path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(path));
    }

    public void Dispose() => Directory.Delete(directory, true);
}
