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

    public void Dispose() => Directory.Delete(directory, true);
}
