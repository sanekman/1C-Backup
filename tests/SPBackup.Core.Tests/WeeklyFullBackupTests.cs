using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

internal sealed class BackupTestClock(string utc) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse(utc, global::System.Globalization.CultureInfo.InvariantCulture);
    public override DateTimeOffset GetUtcNow() => UtcNow;
    public void Set(string utc) => UtcNow = DateTimeOffset.Parse(utc, global::System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class WeeklyFullBackupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "weekly-full-" + Guid.NewGuid());
    private readonly BackupCatalog catalog;
    private readonly BackupTestClock clock = new("2026-10-14T10:00:00Z");
    private readonly BackupJob job;
    private sealed class Unused : IBackupExecutor
    { public Task<string> CreateAsync(BackupJob job, string work, CancellationToken token) => throw new InvalidOperationException(); }
    public WeeklyFullBackupTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "source"));
        File.WriteAllText(Path.Combine(root, "source", "data.txt"), "original");
        catalog = new(Path.Combine(root, "catalog.json"));
        job = new() { Name = "Weekly", Kind = DatabaseKind.FilesAndFolders, Source = Path.Combine(root, "source"),
            DestinationDirectory = Path.Combine(root, "copies"), KeepLast = 1, FileBackupMode = FileBackupMode.Incremental,
            FullBackupEvery = 1, FullBackupDayOfWeek = DayOfWeek.Monday, TimeZoneId = "UTC",
            AdditionalDestinations = [new() { DestinationDirectory = Path.Combine(root, "second"), KeepLast = 1 }] };
    }
    private LocalBackupRunner Runner() => new(new Unused(), new(new FileBackupStorage(), catalog),
        new(Path.Combine(root, "history.json")), Path.Combine(root, "work"), timeProvider: clock);
    private async Task<BackupRun> Run(BackupJob? settings = null)
    {
        var run = await Runner().RunAsync(settings ?? job);
        Assert.True(run.Status == BackupRunStatus.Succeeded, run.Error);
        Assert.NotNull(run.Backup!.FileSnapshot);
        return run;
    }
    [Fact]
    public async Task MissingIndexDoesNotReplaceWeeklyPlanWithAnUnscheduledFull()
    {
        var first = await Run();
        var path = Path.Combine(root, "file-snapshots", job.Id.ToString("N") + ".json");
        File.Delete(path); clock.UtcNow = clock.UtcNow.AddHours(1);
        var next = await Run(); Assert.False(next.Backup!.FileSnapshot!.IsFull);
        Assert.Equal(first.Backup!.FileSnapshot!.SnapshotId, next.Backup.FileSnapshot.ParentSnapshotId);
        File.Delete(path); clock.Set("2026-10-19T00:00:00Z");
        Assert.True((await Run()).Backup!.FileSnapshot!.IsFull);
    }

    [Fact]
    public async Task FirstIsFullThenIncrementsIgnoreArchiveCountAndWeeklyFullRunsOnlyOnce()
    {
        var first = await Run(); Assert.True(first.Backup!.FileSnapshot!.IsFull);
        for (var i = 0; i < 8; i++)
        {
            clock.UtcNow = clock.UtcNow.AddHours(1);
            var inc = await Run(); Assert.False(inc.Backup!.FileSnapshot!.IsFull);
            Assert.Equal(i + 1, inc.Backup.FileSnapshot.Depth);
        }
        clock.Set("2026-10-19T00:00:00Z");
        var monday = await Run(); Assert.True(monday.Backup!.FileSnapshot!.IsFull);
        clock.UtcNow = clock.UtcNow.AddHours(1);
        Assert.False((await Run()).Backup!.FileSnapshot!.IsFull);
        clock.Set("2026-10-20T12:00:00Z");
        var last = await Run(); Assert.False(last.Backup!.FileSnapshot!.IsFull);
        var records = await catalog.ReadAsync();
        var delivered = records.Where(r => r.Backup.FileSnapshot!.SnapshotId == last.Backup.FileSnapshot.SnapshotId).ToArray();
        Assert.Equal(2, delivered.Length);
        foreach (var record in delivered) Assert.Equal(3, FileBackupChains.Resolve(record, records).Count);
        var restore = Path.Combine(root, "restore"); Directory.CreateDirectory(restore);
        var selected = records.First(r => r.Backup.Location == last.Backup.Location);
        var result = await new FileBackupRestorer(new FileBackupStorage(), catalog, Path.Combine(root, "restore-work"))
            .RestoreAsync(selected, job, restore);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(result.Directory, "data.txt")));
    }
    [Theory]
    [InlineData("2026-10-20T00:00:00Z")]
    [InlineData("2026-11-04T00:00:00Z")]
    public async Task MissedWeekdayCreatesOneFullOnNextRun(string next)
    {
        await Run(); clock.Set(next);
        Assert.True((await Run()).Backup!.FileSnapshot!.IsFull);
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        Assert.False((await Run()).Backup!.FileSnapshot!.IsFull);
    }
    [Fact]
    public async Task WeekdayUsesJobTimeZoneAndFirstRunOnWeekdayDoesNotRepeatFull()
    {
        var local = job with { TimeZoneId = "Asia/Vladivostok" };
        clock.Set("2026-10-18T13:59:00Z"); await Run(local); // Sunday 23:59 local.
        clock.Set("2026-10-18T14:00:00Z");
        Assert.True((await Run(local)).Backup!.FileSnapshot!.IsFull); // Monday 00:00 local.
        clock.Set("2026-10-18T14:01:00Z");
        Assert.False((await Run(local)).Backup!.FileSnapshot!.IsFull);
    }
    [Theory]
    [InlineData(DayOfWeek.Monday, "2026-10-19T10:00:00Z")]
    [InlineData(DayOfWeek.Tuesday, "2026-10-20T10:00:00Z")]
    [InlineData(DayOfWeek.Wednesday, "2026-10-21T10:00:00Z")]
    [InlineData(DayOfWeek.Thursday, "2026-10-15T10:00:00Z")]
    [InlineData(DayOfWeek.Friday, "2026-10-16T10:00:00Z")]
    [InlineData(DayOfWeek.Saturday, "2026-10-17T10:00:00Z")]
    [InlineData(DayOfWeek.Sunday, "2026-10-18T10:00:00Z")]
    public async Task EachWeekdayCreatesFullAtItsNextOccurrence(DayOfWeek day, string due)
    {
        var configured = job with { FullBackupDayOfWeek = day };
        await Run(configured);
        Assert.False((await Run(configured)).Backup!.FileSnapshot!.IsFull);
        clock.Set(due); Assert.True((await Run(configured)).Backup!.FileSnapshot!.IsFull);
        Assert.False((await Run(configured)).Backup!.FileSnapshot!.IsFull);
    }
    [Fact]
    public async Task InitialFullOnChosenDayAndYearBoundaryFollowWeeklyPlan()
    {
        clock.Set("2026-12-28T10:00:00Z"); Assert.True((await Run()).Backup!.FileSnapshot!.IsFull);
        clock.UtcNow = clock.UtcNow.AddHours(1); Assert.False((await Run()).Backup!.FileSnapshot!.IsFull);
        clock.Set("2027-01-03T10:00:00Z"); Assert.False((await Run()).Backup!.FileSnapshot!.IsFull);
        clock.Set("2027-01-04T10:00:00Z"); Assert.True((await Run()).Backup!.FileSnapshot!.IsFull);
    }
    [Fact]
    public async Task FailedDueFullDoesNotConsumeWeeklyRun()
    {
        await Run(); clock.Set("2026-10-19T10:00:00Z");
        Directory.Move(job.Source, job.Source + "-offline");
        Assert.Equal(BackupRunStatus.Failed, (await Runner().RunAsync(job)).Status);
        Directory.Move(job.Source + "-offline", job.Source);
        clock.Set("2026-10-20T10:00:00Z"); Assert.True((await Run()).Backup!.FileSnapshot!.IsFull);
        Assert.False((await Run()).Backup!.FileSnapshot!.IsFull);
    }
    [Fact]
    public async Task RemovingWeekdayRestoresCountBasedPlanAndSettingRoundTrips()
    {
        var store = new JobStore(Path.Combine(root, "jobs.json")); await store.SaveAsync([job]);
        Assert.Equal(DayOfWeek.Monday, (await store.LoadAsync()).Single().FullBackupDayOfWeek);
        await Run();
        var count = job with { FullBackupDayOfWeek = null, FullBackupEvery = 2 };
        await store.SaveAsync([count]);
        Assert.Null((await store.LoadAsync()).Single().FullBackupDayOfWeek);
        Assert.DoesNotContain("FullBackupDayOfWeek", await File.ReadAllTextAsync(Path.Combine(root, "jobs.json")));
        Assert.False((await Run(count)).Backup!.FileSnapshot!.IsFull);
        Assert.True((await Run(count)).Backup!.FileSnapshot!.IsFull);
    }
    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    public void InvalidWeekdayIsRejected(int value) => Assert.NotEmpty((job with { FullBackupDayOfWeek = (DayOfWeek)value }).Validate());
    public void Dispose() => Directory.Delete(root, true);
}
