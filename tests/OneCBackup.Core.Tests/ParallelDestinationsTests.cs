using System.Collections.Concurrent;
using System.Threading.Channels;
using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class ParallelDestinationsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OneCBackup-parallel-" + Guid.NewGuid());
    private BackupJob Job => new() { Id = Guid.NewGuid(), Name = "Parallel", Source = "database", ArchiveCopies = true,
        DestinationDirectory = Path.Combine(root, "0"), KeepLast = 1,
        AdditionalDestinations = Enumerable.Range(1, 7).Select(index => new BackupDestination
            { DestinationDirectory = Path.Combine(root, index.ToString()), KeepLast = index + 1 }).ToArray() };

    [Fact]
    public async Task ThreeDeliveriesOverlapUseOneArchiveAndResultsKeepConfiguredOrder()
    {
        Directory.CreateDirectory(root);
        var job = Job;
        var storage = new GatedStorage();
        var executor = new Executor();
        var catalog = new BackupCatalog(Path.Combine(root, "catalog.json"));
        var runner = new LocalBackupRunner(executor, new BackupCompletion(storage, catalog),
            new BackupRunHistory(Path.Combine(root, "history.json")), Path.Combine(root, "work"));
        var task = runner.RunAsync(job);
        try
        {
            var first = await storage.NextAsync();
            var second = await storage.NextAsync();
            var third = await storage.NextAsync();
            Assert.Equal(3, storage.Paths.Count);
            Assert.False(task.IsCompleted);
            Assert.Single(storage.Paths.Values.Distinct());
            // Complete a later target first: the next queued target must start while the first stays blocked.
            storage.Release(third);
            var fourth = await storage.NextAsync();
            Assert.Equal(job.AdditionalDestinations![2].DestinationDirectory, fourth);
            Assert.False(task.IsCompleted);
            storage.Release(second);
            storage.Release(first);
            storage.Release(fourth);
            for (var index = 0; index < 4; index++) storage.Release(await storage.NextAsync());
            var run = await task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(BackupRunStatus.Succeeded, run.Status);
            Assert.Equal(1, executor.Count);
            Assert.Single(storage.Paths.Values.Distinct());
            Assert.Equal(job.StorageJobs.Select(target => target.DestinationDirectory),
                run.Destinations!.Select(result => Path.GetDirectoryName(result.Record!.Backup.Location)));
            Assert.Equal(run.Destinations![0].Record!.Backup, run.Backup);
            Assert.Equal(8, (await catalog.ReadAsync()).Count);
            Assert.False(Directory.Exists(run.WorkDirectory));
        }
        finally { storage.ReleaseAll(); await task; }
    }

    [Fact]
    public async Task FailedTargetDoesNotWaitForOrStopBlockedTarget()
    {
        Directory.CreateDirectory(root);
        var job = Job;
        var storage = new GatedStorage { FailedDirectory = job.DestinationDirectory };
        var catalog = new BackupCatalog(Path.Combine(root, "catalog.json"));
        var runner = new LocalBackupRunner(new Executor(), new BackupCompletion(storage, catalog),
            new BackupRunHistory(Path.Combine(root, "history.json")), Path.Combine(root, "work"));
        var task = runner.RunAsync(job);
        try
        {
            for (var index = 0; index < 3; index++) await storage.NextAsync();
            storage.Release(job.DestinationDirectory);
            var fourth = await storage.NextAsync();
            Assert.False(task.IsCompleted);
            storage.ReleaseAll();
            var run = await task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(BackupRunStatus.PartiallySucceeded, run.Status);
            Assert.NotNull(run.Destinations![0].Error);
            Assert.All(run.Destinations.Skip(1), result => Assert.NotNull(result.Record));
            Assert.Equal(7, (await catalog.ReadAsync()).Count);
            Assert.True(Directory.Exists(run.WorkDirectory));
        }
        finally { storage.ReleaseAll(); await task; }
    }

    [Fact]
    public async Task CancellationWaitsForActiveCleanupAndDoesNotStartQueuedDestinations()
    {
        Directory.CreateDirectory(root);
        using var cancellation = new CancellationTokenSource();
        var storage = new GatedStorage { WaitForCancellation = true };
        var catalog = new BackupCatalog(Path.Combine(root, "catalog.json"));
        var history = new BackupRunHistory(Path.Combine(root, "history.json"));
        var runner = new LocalBackupRunner(new Executor(), new BackupCompletion(storage, catalog), history,
            Path.Combine(root, "work"));
        var task = runner.RunAsync(Job, cancellation.Token);
        try
        {
            for (var index = 0; index < 3; index++) await storage.NextAsync();
            cancellation.Cancel();
            await storage.AllCancelling.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(task.IsCompleted);
            Assert.All(storage.Paths.Values, path => Assert.True(File.Exists(path)));
            storage.CleanupRelease.TrySetResult();
            var run = await task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(BackupRunStatus.Cancelled, run.Status);
            Assert.Equal(3, storage.Paths.Count);
            Assert.Equal(3, storage.Cleaned);
            Assert.Equal(8, run.Destinations!.Count);
            Assert.All(run.Destinations, result => Assert.NotNull(result.Error));
            Assert.Empty(await catalog.ReadAsync());
            Assert.Equal(BackupRunStatus.Cancelled, Assert.Single(await history.ReadAsync()).Status);
            Assert.True(Directory.Exists(run.WorkDirectory));
        }
        finally { cancellation.Cancel(); storage.CleanupRelease.TrySetResult(); await task; }
    }

    private sealed class Executor : IBackupExecutor
    {
        public int Count { get; private set; }
        public async Task<string> CreateAsync(BackupJob job, string workingDirectory, CancellationToken cancellationToken = default)
        {
            Count++;
            var path = Path.Combine(workingDirectory, "database.bak");
            await File.WriteAllTextAsync(path, "one shared backup", cancellationToken);
            return path;
        }
    }

    private sealed class GatedStorage : IBackupStorage
    {
        private readonly FileBackupStorage files = new();
        private readonly Channel<string> entered = Channel.CreateUnbounded<string>();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> gates = new();
        private readonly TaskCompletionSource releaseAll = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int cancelling, cleaned;
        public ConcurrentDictionary<string, string> Paths { get; } = new();
        public string? FailedDirectory { get; init; }
        public bool WaitForCancellation { get; init; }
        public int Cleaned => Volatile.Read(ref cleaned);
        public TaskCompletionSource AllCancelling { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> NextAsync() => entered.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public void Release(string destination) => gates[destination].TrySetResult();
        public void ReleaseAll() => releaseAll.TrySetResult();
        public async Task<StoredBackup> StoreAsync(string path, BackupJob job, CancellationToken cancellationToken = default)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gates[job.DestinationDirectory] = gate;
            Paths[job.DestinationDirectory] = path;
            await entered.Writer.WriteAsync(job.DestinationDirectory, cancellationToken);
            if (WaitForCancellation)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException)
                {
                    if (Interlocked.Increment(ref cancelling) == 3) AllCancelling.TrySetResult();
                    await CleanupRelease.Task;
                    Interlocked.Increment(ref cleaned);
                    throw;
                }
            }
            await Task.WhenAny(gate.Task, releaseAll.Task).WaitAsync(cancellationToken);
            if (job.DestinationDirectory == FailedDirectory) throw new IOException("Unavailable target");
            return await files.StoreAsync(path, job, cancellationToken);
        }
        public Task VerifyAsync(StoredBackup backup, BackupJob job, CancellationToken cancellationToken = default) => files.VerifyAsync(backup, job, cancellationToken);
        public Task DeleteAsync(StoredBackup backup, BackupJob job, CancellationToken cancellationToken = default) => files.DeleteAsync(backup, job, cancellationToken);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
