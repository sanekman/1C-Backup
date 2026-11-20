using System.Net;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class LiveJobUpdateTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "spbackup-live-profile-" + Guid.NewGuid());
    private JobStore Store => new(Path.Combine(root, "jobs.json"), useLocalTime: true);
    private BackupJob Job => new() { Name = "before", Kind = DatabaseKind.PostgreSql, Source = "fixture",
        DestinationDirectory = Path.Combine(root, "copies"), ScheduleKind = ScheduleKind.SystemStartup,
        PostgreSql = new() { PgDumpPath = Path.Combine(root, "pg_dump"), PgRestorePath = Path.Combine(root, "pg_restore") } };
    [Fact]
    public async Task ServiceOwnerAppliesProtectedSettingsDuringRunWithoutRestartOrCancellingOldSnapshot()
    {
        Directory.CreateDirectory(root); var job = Job; await Store.SaveAsync([job]);
        var executor = new Blocking(); using var stop = new CancellationTokenSource();
        var host = new LocalAgentHost(root, executor, new NoCredentials()).RunAsync(pollInterval: TimeSpan.FromMilliseconds(30), cancellationToken: stop.Token);
        try
        {
            await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var state = (await new LocalAgentControlStore(root).ReadAsync())!;
            Assert.True(state.CanUpdateLocalJobs);
            var before = await Store.LoadAsync();
            var changed = job with { Name = "after", PostgreSql = job.PostgreSql! with { Tls = new() } };
            await new LocalJobUpdates(root).ApplyAsync(state, before, [changed]);
            var applied = Assert.Single(await Store.LoadAsync());
            Assert.Equal("after", applied.Name); Assert.NotNull(applied.PostgreSql!.Tls);
            Assert.DoesNotContain(job.Id.ToString(), await File.ReadAllTextAsync(Path.Combine(root, "jobs.json")));
            Assert.False(executor.Token.IsCancellationRequested); Assert.False(host.IsCompleted);
            Assert.Equal("before", executor.Snapshot!.Name); Assert.Null(executor.Snapshot.PostgreSql!.Tls);
            Assert.Equal(state.SessionId, (await new LocalAgentControlStore(root).ReadAsync())!.SessionId);
            executor.Finish.TrySetResult();
            await Wait(async () => (await new BackupRunHistory(Path.Combine(root, "runs.json")).ReadAsync()).Any(r => r.Status == BackupRunStatus.Succeeded));
            var request = await new LocalManualRunQueue(root).EnqueueAsync(state.SessionId, job.Id);
            await Wait(() => Task.FromResult(executor.Names.Count >= 2));
            Assert.Equal(new[] { "before", "after" }, executor.Names.ToArray());
            Assert.NotNull(executor.Snapshot!.PostgreSql!.Tls);
        }
        finally { stop.Cancel(); await host.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    [Fact]
    public async Task StaleEditorCannotOverwriteAnotherAcceptedChangeAndRetryIsIdempotent()
    {
        Directory.CreateDirectory(root); var job = Job with { ScheduleKind = ScheduleKind.Manual }; await Store.SaveAsync([job]);
        using var stop = new CancellationTokenSource();
        var host = new LocalAgentHost(root, new Blocking(), new NoCredentials()).RunAsync(pollInterval: TimeSpan.FromMilliseconds(30), cancellationToken: stop.Token);
        try
        {
            LocalAgentState? state = null;
            await Wait(async () => (state = await new LocalAgentControlStore(root).ReadAsync())?.CanUpdateLocalJobs == true);
            var before = await Store.LoadAsync(); var changed = job with { Name = "first editor" };
            var updates = new LocalJobUpdates(root); await updates.ApplyAsync(state!, before, [changed]);
            await updates.ApplyAsync(state!, before, [changed]);
            await Assert.ThrowsAsync<IOException>(() => updates.ApplyAsync(state!, before, [job with { Name = "stale overwrite" }]));
            Assert.Equal("first editor", Assert.Single(await Store.LoadAsync()).Name);
            await Assert.ThrowsAsync<IOException>(() => updates.ApplyAsync(state! with { SessionId = Guid.NewGuid() }, [changed], [job]));
            Assert.Equal("first editor", Assert.Single(await Store.LoadAsync()).Name);
        }
        finally { stop.Cancel(); await host.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    [Fact]
    public async Task MalformedPendingFilesDoNotBlockFollowingValidSave()
    {
        Directory.CreateDirectory(root); var job = Job with { ScheduleKind = ScheduleKind.Manual }; await Store.SaveAsync([job]);
        var pending = Path.Combine(root, "job-updates", "pending"); Directory.CreateDirectory(pending);
        await File.WriteAllTextAsync(Path.Combine(pending, Guid.NewGuid().ToString("N") + ".json"), "{");
        await File.WriteAllTextAsync(Path.Combine(pending, Guid.NewGuid().ToString("N") + ".json"), "null");
        using var stop = new CancellationTokenSource();
        var host = new LocalAgentHost(root, new Blocking(), new NoCredentials()).RunAsync(pollInterval: TimeSpan.FromMilliseconds(30), cancellationToken: stop.Token);
        try
        {
            LocalAgentState? state = null;
            await Wait(async () => (state = await new LocalAgentControlStore(root).ReadAsync())?.CanUpdateLocalJobs == true);
            var changed = job with { Name = "valid after corrupt request" };
            await new LocalJobUpdates(root).ApplyAsync(state!, await Store.LoadAsync(), [changed]);
            Assert.Equal(changed.Name, Assert.Single(await Store.LoadAsync()).Name);
            Assert.Empty(Directory.EnumerateFiles(pending, "*.json"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => new LocalJobUpdates(root).ApplyAsync(
                state! with { ExecutionMode = AgentMode.Managed }, [changed], [job]));
            Assert.Equal(changed.Name, Assert.Single(await Store.LoadAsync()).Name);
        }
        finally { stop.Cancel(); await host.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    private static async Task Wait(Func<Task<bool>> test)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!await test()) await Task.Delay(30, deadline.Token);
    }
    private sealed class NoCredentials : IFtpCredentialStore
    { public Task<NetworkCredential> GetAsync(string key, CancellationToken token = default) => throw new InvalidOperationException(); }
    private sealed class Blocking : IBackupExecutor
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Names = new();
        public BackupJob? Snapshot; public CancellationToken Token;
        public async Task<string> CreateAsync(BackupJob job, string work, CancellationToken token)
        {
            Snapshot = job; Token = token; Names.Enqueue(job.Name); Started.TrySetResult();
            await Finish.Task.WaitAsync(token); Directory.CreateDirectory(work);
            var path = Path.Combine(work, Guid.NewGuid()+".dump"); await File.WriteAllTextAsync(path, job.Name, token);return path;
        }
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
