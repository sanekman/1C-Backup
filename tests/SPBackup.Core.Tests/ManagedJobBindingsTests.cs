using System.Text.Json;
using SPBackup.Core;
using Xunit;
namespace SPBackup.Core.Tests;
public sealed class ManagedJobBindingsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "managed-access-" + Guid.NewGuid().ToString("N"));
    private readonly BackupJob job = new() { Name = "Protected", Kind = DatabaseKind.FilesAndFolders, Source = @"C:\Data",
        StorageKind = StorageKind.NetworkShare, DestinationDirectory = @"\\nas\copies\a", ManagedSecrets = new(true, [0]),
        AdditionalDestinations = [new() { StorageKind = StorageKind.NetworkShare, DestinationDirectory = @"\\nas2\copy\a" }] };
    [Fact]
    public async Task LocalReferencesResolveOnlyForExactSourceAndDestinationScope()
    {
        var store = new ManagedJobBindings(root);
        Assert.False(ManagedJobBindings.Ready(await store.ResolveAsync(job)));
        await store.SaveAsync(job, new(job.Id, ManagedJobBindings.Scope(job), "archive-key", new Dictionary<int,string> { [0] = "smb-key" }));
        var resolved = await store.ResolveAsync(job);
        Assert.True(ManagedJobBindings.Ready(resolved)); Assert.Equal("archive-key", resolved.ArchivePasswordKey);
        Assert.Equal("smb-key", resolved.NetworkShare!.CredentialKey);
        Assert.Null(resolved.AdditionalDestinations![0].NetworkShare);
        Assert.True(ManagedJobBindings.Ready(await store.ResolveAsync(job with { Name = "Renamed", DailyAt = new(4, 0) })));
        Assert.False(ManagedJobBindings.Ready(await store.ResolveAsync(job with { Source = @"C:\Other" })));
        Assert.False(ManagedJobBindings.Ready(await store.ResolveAsync(job with { DestinationDirectory = @"\\nas\copies\other" })));
        Assert.False(ManagedJobBindings.Ready(await store.ResolveAsync(resolved with { Source = @"C:\Other" })));
        Assert.DoesNotContain("archive-key", JsonSerializer.Serialize(job));
        Assert.DoesNotContain("smb-key", JsonSerializer.Serialize(job));
    }
    [Fact]
    public async Task OtherEnrollmentAndCorruptBindingCannotInheritAccess()
    {
        var store = new ManagedJobBindings(root);
        await store.SaveAsync(job, new(job.Id, ManagedJobBindings.Scope(job), "archive-key", new Dictionary<int,string> { [0] = "smb-key" }));
        Assert.False(ManagedJobBindings.Ready(await new ManagedJobBindings(root + "-new").ResolveAsync(job)));
        await File.WriteAllTextAsync(Path.Combine(root, job.Id.ToString("N") + ".json"), "broken");
        Assert.False(ManagedJobBindings.Ready(await store.ResolveAsync(job)));
    }
    [Fact]
    public async Task MissingRequiredKeysAreNotSaved()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => new ManagedJobBindings(root)
            .SaveAsync(job, new(job.Id, ManagedJobBindings.Scope(job), null, new Dictionary<int,string>())));
        Assert.False(Directory.Exists(root));
    }
    private sealed class NeverRun : IBackupExecutor
    {
        public Task<string> CreateAsync(BackupJob job, string workDirectory, CancellationToken token = default) => throw new Exception("Protected source must not be read");
    }
    [Fact]
    public async Task UnboundRequiredPasswordFailsBeforeSourceOrStorageAndPreservesOldCopies()
    {
        Directory.CreateDirectory(Path.Combine(root, "copies"));
        var old = Path.Combine(root, "copies", "old.zip"); await File.WriteAllTextAsync(old, "previous copy");
        var selected = new BackupJob { Name = "Protected", Kind = DatabaseKind.OneCFile, OneCMethod = OneCBackupMethod.FileArchive,
            Source = Path.Combine(root, "source"), DestinationDirectory = Path.Combine(root, "copies"), ManagedSecrets = new(true, []) };
        var history = new BackupRunHistory(Path.Combine(root, "runs.json"));
        var catalog = new BackupCatalog(Path.Combine(root, "catalog.json"));
        var run = await new LocalBackupRunner(new NeverRun(), new(new FileBackupStorage(), catalog), history, Path.Combine(root, "work")).RunAsync(selected);
        Assert.Equal(BackupRunStatus.Failed, run.Status); Assert.Contains("Не задан защищённый доступ", run.Error);
        Assert.Equal("previous copy", await File.ReadAllTextAsync(old)); Assert.Empty(await catalog.ReadAsync());
        Assert.Single(Directory.EnumerateFiles(Path.Combine(root, "copies")));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
