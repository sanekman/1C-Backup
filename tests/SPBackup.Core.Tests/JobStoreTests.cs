using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class JobStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OneCBackup-tests-" + Guid.NewGuid());
    private string StorePath => Path.Combine(directory, "jobs.json");
    private static BackupJob Job(DatabaseKind kind = DatabaseKind.OneCFile) => new()
    {
        Name = "Бухгалтерия", Kind = kind, Source = "TestDatabase",
        DestinationDirectory = "D:\\Backups", DailyAt = new TimeOnly(22, 15), TimeZoneId = "UTC"
    };

    [Theory]
    [InlineData(DatabaseKind.OneCFile)]
    [InlineData(DatabaseKind.SqlServer)]
    [InlineData(DatabaseKind.PostgreSql)]
    public async Task SettingsSurviveReload(DatabaseKind kind)
    {
        var job = Job(kind);
        await new JobStore(StorePath).SaveAsync([job]);
        Assert.Equal(job, Assert.Single(await new JobStore(StorePath).LoadAsync()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArchiveSettingSurvivesReload(bool enabled)
    {
        var job = Job() with { ArchiveCopies = enabled };
        await new JobStore(StorePath).SaveAsync([job]);
        Assert.Equal(enabled, Assert.Single(await new JobStore(StorePath).LoadAsync()).ArchiveCopies);
    }

    [Fact]
    public async Task OlderJobsLoadWithArchivingDisabled()
    {
        await new JobStore(StorePath).SaveAsync([Job()]);
        var document = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(StorePath))!;
        document[0]!.AsObject().Remove("ArchiveCopies");
        await File.WriteAllTextAsync(StorePath, document.ToJsonString());
        Assert.False(Assert.Single(await new JobStore(StorePath).LoadAsync()).ArchiveCopies);
    }

    [Fact]
    public async Task InvalidSettingsDoNotOverwriteExistingJobs()
    {
        var store = new JobStore(StorePath);
        var job = Job();
        await store.SaveAsync([job]);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync([job with { KeepLast = 0 }]));
        Assert.Equal(job, Assert.Single(await store.LoadAsync()));
    }

    [Fact]
    public async Task DuplicateIdentifiersAreRejected()
    {
        var job = Job();
        await Assert.ThrowsAsync<InvalidDataException>(() => new JobStore(StorePath).SaveAsync([job, job with { Name = "Другая база" }]));
        Assert.False(File.Exists(StorePath));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{\"Id\":\"00000000-0000-0000-0000-000000000000\"}]")]
    public async Task InvalidSavedDocumentIsReported(string contents)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(StorePath, contents);
        await Assert.ThrowsAsync<InvalidDataException>(() => new JobStore(StorePath).LoadAsync());
        Assert.Equal(contents, await File.ReadAllTextAsync(StorePath));
    }

    [Fact]
    public async Task CancelledSavePreservesPreviousDocument()
    {
        var store = new JobStore(StorePath);
        var job = Job();
        await store.SaveAsync([job]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync([job with { Name = "Изменено" }], cancellation.Token));
        Assert.Equal(job, Assert.Single(await store.LoadAsync()));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void UnknownTimeZoneAndDatabaseKindAreRejected()
    {
        Assert.NotEmpty((Job() with { TimeZoneId = "invalid/not-a-zone" }).Validate());
        Assert.NotEmpty((Job() with { Kind = (DatabaseKind)99 }).Validate());
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
