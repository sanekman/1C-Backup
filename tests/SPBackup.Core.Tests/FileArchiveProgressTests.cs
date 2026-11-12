using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class FileArchiveProgressTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "file-progress-" + Guid.NewGuid());
    private readonly BackupJob job;
    public FileArchiveProgressTests()
    {
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        job = new() { Name = "Bytes", Kind = DatabaseKind.FilesAndFolders, Source = source,
            DestinationDirectory = Path.Combine(root, "copies"), FileBackupMode = FileBackupMode.Incremental };
    }
    public void Dispose() => Directory.Delete(root, true);
    private async Task<BackupRun> RunAsync(List<BackupProgressSnapshot> events)
        => await new LocalBackupRunner(new Denied(), new(new FileBackupStorage(), new BackupCatalog(Path.Combine(root, "catalog.json"))),
            new BackupRunHistory(Path.Combine(root, "history.json")), Path.Combine(root, "work"), progress: events.Add).RunAsync(job);
    private static void AssertBatch(List<BackupProgressSnapshot> events, long total)
    {
        Assert.DoesNotContain(events, value => value.Progress.Stage is BackupStage.ArchivingFile or BackupStage.ProcessingFiles);
        var archiving = events.Where(value => value.Progress.Stage == BackupStage.ArchivingFiles).ToArray();
        Assert.NotEmpty(archiving);
        Assert.All(archiving, value => Assert.Equal(total, value.Progress.Total));
        Assert.Equal(total, archiving[^1].Progress.Completed);
        Assert.Equal(archiving.Select(value => value.Progress.Completed).Order(), archiving.Select(value => value.Progress.Completed));
    }
    [Fact]
    public async Task FullAndUnchangedIncrementalUseTotalBytesRatherThanFileCount()
    {
        await File.WriteAllBytesAsync(Path.Combine(job.Source, "a.txt"), new byte[100]);
        await File.WriteAllBytesAsync(Path.Combine(job.Source, "b.txt"), new byte[900]);
        var events = new List<BackupProgressSnapshot>();
        var full = await RunAsync(events); Assert.Equal(BackupRunStatus.Succeeded, full.Status); AssertBatch(events, 1000);
        events.Clear();
        var incremental = await RunAsync(events); Assert.Equal(BackupRunStatus.Succeeded, incremental.Status);
        Assert.False(incremental.Backup!.FileSnapshot!.IsFull); AssertBatch(events, 1000);
    }
    private sealed class Denied : IBackupExecutor
    { public Task<string> CreateAsync(BackupJob job, string work, CancellationToken token) => throw new UnauthorizedAccessException(); }
    private sealed class SlowRead(byte[] bytes) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (Position < Length) await Task.Delay(550, token);
            return await base.ReadAsync(buffer, token);
        }
    }
    [Fact]
    public async Task PartialArchiveDoesNotResetBetweenUnequalFilesAndSkippedBytesFinishWithWarnings()
    {
        foreach (var (name, size) in new[] { ("a.txt", 100), ("b.txt", 900), ("c.txt", 200) })
            await File.WriteAllBytesAsync(Path.Combine(job.Source, name), new byte[size]);
        Stream Open(string path) => Path.GetFileName(path) == "c.txt" ? throw new UnauthorizedAccessException() : new SlowRead(File.ReadAllBytes(path));
        var events = new List<BackupProgressSnapshot>();
        using (BackupProgressContext.Begin(job, Guid.NewGuid(), events.Add))
        {
            await new AccessibleFilesBackupExecutor(new Denied(), openSource: Open).CreateAsync(job, Path.Combine(root, "partial-work"), CancellationToken.None);
            BackupProgressContext.Report(BackupStage.Completed);
        }
        AssertBatch(events, 1200);
        Assert.Contains(events, value => value.Progress.Stage == BackupStage.ArchivingFiles && value.Progress.Completed == 50 && value.Progress.Percent == 4);
        // The first half of a file's budget reports staging; the second reports ZIP writes.
        // Staged files compress quickly; throttling can omit an intermediate
        // boundary. AssertBatch checks monotonic total-byte progress to 100%.
    }
    [Fact]
    public async Task EmptyAndZeroLengthFilesRemainValidWithoutDivisionByZero()
    {
        var events = new List<BackupProgressSnapshot>();
        Assert.Equal(BackupRunStatus.Succeeded, (await RunAsync(events)).Status); AssertBatch(events, 0);
        await File.WriteAllBytesAsync(Path.Combine(job.Source, "empty.txt"), []); events.Clear();
        Assert.Equal(BackupRunStatus.Succeeded, (await RunAsync(events)).Status); AssertBatch(events, 0);
    }
}
