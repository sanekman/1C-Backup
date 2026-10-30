using System.Net;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class NetworkShareTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sp-network-" + Guid.NewGuid().ToString("N"));
    private static BackupJob Job() => new() { Name = "NAS fixture", Source = "fixture", StorageKind = StorageKind.NetworkShare,
        DestinationDirectory = @"\\nas\Backups\Company", ScheduleKind = ScheduleKind.Manual,
        NetworkShare = new() { ShareRoot = @"\\nas\Backups", CredentialKey = "fixture-reference" } };

    [Theory]
    [InlineData(@"\\NAS\Backups", @"\\NAS\Backups")]
    [InlineData(@"\\nas\Backups\Company", @"\\nas\Backups")]
    [InlineData(@"\\192.0.2.10\Shared\Folder\", @"\\192.0.2.10\Shared")]
    [InlineData(@"\\nas\Общие файлы\Бухгалтерия", @"\\nas\Общие файлы")]
    public void CredentialsBindToTheShareRatherThanItsSubdirectory(string path, string expected) => Assert.Equal(expected, NetworkShareSettings.Root(path));

    [Theory]
    [InlineData(@"C:\Backups")]
    [InlineData(@"\\nas")]
    [InlineData(@"\\nas\share\..\other")]
    [InlineData("//nas/share")]
    [InlineData(@"\\nas\share\folder:stream")]
    [InlineData("\\\\nas\\share\\bad\nname")]
    public void AmbiguousNonUncAndTraversalPathsCannotReceiveAnExplicitLogin(string path) => Assert.Throws<ArgumentException>(() => NetworkShareSettings.Root(path));

    [Fact]
    public void ChangingServerShareOrStorageTypeInvalidatesTheCredentialBinding()
    {
        var job = Job(); Assert.Empty(job.Validate());
        Assert.Empty((job with { DestinationDirectory = @"\\NAS\BACKUPS\Another" }).Validate());
        Assert.NotEmpty((job with { DestinationDirectory = @"\\other\Backups\Company" }).Validate());
        Assert.NotEmpty((job with { DestinationDirectory = @"\\nas\other\Company" }).Validate());
        Assert.NotEmpty((job with { StorageKind = StorageKind.LocalDirectory }).Validate());
        Assert.NotEmpty((job with { NetworkShare = job.NetworkShare! with { CredentialKey = "" } }).Validate());
    }

    [Fact]
    public void AdditionalDestinationsNeverInheritAnotherSharesNetworkLogin()
    {
        var primary = Job();
        var ordinary = new BackupDestination { StorageKind = StorageKind.LocalDirectory, DestinationDirectory = root };
        Assert.Null(ordinary.ApplyTo(primary).NetworkShare);
        var secondary = new BackupDestination { StorageKind = StorageKind.NetworkShare, DestinationDirectory = @"\\second\copies",
            NetworkShare = new() { ShareRoot = @"\\second\copies", CredentialKey = "different-reference" } };
        Assert.Equal("different-reference", secondary.ApplyTo(primary).NetworkShare!.CredentialKey);
        Assert.Contains("отдельный сетевой вход", secondary.Description);
    }

    [Fact]
    public async Task ExplicitNetworkJobsStayOutsideTheLegacyJobFileAndSurviveReload()
    {
        var path = Path.Combine(root, "jobs.json"); var store = new JobStore(path);
        var ordinary = Job() with { Id = Guid.NewGuid(), StorageKind = StorageKind.LocalDirectory, DestinationDirectory = root, NetworkShare = null };
        var network = Job(); await store.SaveAsync([ordinary, network]);
        var legacy = await File.ReadAllTextAsync(path);
        Assert.Contains(ordinary.Id.ToString(), legacy); Assert.DoesNotContain(network.Id.ToString(), legacy);
        var loaded = await store.LoadAsync(); Assert.Equal(2, loaded.Count);
        Assert.Equal(network, loaded.Single(j => j.Id == network.Id));
        Assert.Equal("fixture-reference", loaded.Single(j => j.Id == network.Id).NetworkShare!.CredentialKey);
    }

    [Fact]
    public async Task IntroducingANetworkLoginRequiresStoppingTheCurrentExecutor()
    {
        var store = new JobStore(Path.Combine(root, "jobs.json")); var job = Job() with { NetworkShare = null };
        await store.SaveAsync([job]);
        using (var lease = new BackupEngineCoordinator(root).Acquire())
            await Assert.ThrowsAnyAsync<IOException>(() => store.SaveAsync([job with { NetworkShare = Job().NetworkShare }]));
        Assert.Null(Assert.Single(await store.LoadAsync()).NetworkShare);
        await store.SaveAsync([job with { NetworkShare = Job().NetworkShare }]); Assert.NotNull(Assert.Single(await store.LoadAsync()).NetworkShare);
    }

    [Fact]
    public async Task NonWindowsHostsNeverFallBackToTheCurrentUserForAnExplicitWindowsLogin()
    {
        if (OperatingSystem.IsWindows()) return;
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => new RoutedBackupStorage(new NeverRead(), root).PrepareAsync(Job()));
    }
    private sealed class NeverRead : IFtpCredentialStore
    { public Task<NetworkCredential> GetAsync(string key, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected FTP credentials read."); }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
