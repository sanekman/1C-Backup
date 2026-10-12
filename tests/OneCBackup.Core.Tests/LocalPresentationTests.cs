using System.Globalization;
using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class LocalPresentationTests
{
    [Theory]
    [InlineData("Бухгалтерия / 1С:*?", "Бухгалтерия _ 1С___")]
    [InlineData("CON", "CON")]
    [InlineData(" . ", "Backup")]
    public void BackupNameIsWindowsSafeAndUsesLocalDate(string name, string expected)
    {
        var created = new DateTimeOffset(2026, 10, 12, 14, 15, 16, TimeSpan.FromHours(10));
        var id = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        var actual = BackupFileName.Create(new() { Name = name }, ".bak.zip", created, id);
        Assert.Equal(expected + "_" + created.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + "_123456.bak.zip", actual);
        Assert.DoesNotContain(actual, c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c));
    }

    [Fact]
    public void RepeatedRunsWithSameNameAndDateHaveDifferentNames()
    {
        var job = new BackupJob { Name = "Бухгалтерия" };
        var instant = DateTimeOffset.Now;
        Assert.NotEqual(BackupFileName.Create(job, ".dt", instant), BackupFileName.Create(job, ".dt", instant));
        Assert.True(BackupFileName.Create(job with { Name = new string('я', 200) }, ".1CD.zip").Length < 130);
    }

    [Fact]
    public async Task StandaloneStoreMigratesLegacyZoneWithoutChangingManagedStoreOrDataOnRead()
    {
        var root = Path.Combine(Path.GetTempPath(), "onec-local-clock-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "jobs.json");
            var legacy = new BackupJob { Name = "База", Source = "base", DestinationDirectory = root,
                DailyAt = new TimeOnly(23, 0), TimeZoneId = "Asia/Vladivostok" };
            await new JobStore(path).SaveAsync([legacy]);
            var before = await File.ReadAllTextAsync(path);
            var localStore = new JobStore(path, useLocalTime: true);
            var job = Assert.Single(await localStore.LoadAsync());
            Assert.Equal(BackupSchedule.LocalTimeZone, job.TimeZoneId);
            Assert.Equal(before, await File.ReadAllTextAsync(path));
            Assert.Equal(legacy.TimeZoneId, Assert.Single(await new JobStore(path).LoadAsync()).TimeZoneId);
            var after = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
            Assert.Equal(BackupSchedule.NextRun(job with { TimeZoneId = TimeZoneInfo.Local.Id }, after), BackupSchedule.NextRun(job, after));
            await localStore.SaveAsync([job with { TimeZoneId = "UTC" }]);
            Assert.Equal(BackupSchedule.LocalTimeZone, Assert.Single(await new JobStore(path).LoadAsync()).TimeZoneId);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
