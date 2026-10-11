using OneCBackup.Core;
using System.Runtime.Versioning;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class WindowsCredentialTheoryAttribute : TheoryAttribute
{
    public WindowsCredentialTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Native Windows credential manager requires Windows.";
    }
}

public sealed class WindowsCredentialTests
{
    [WindowsCredentialTheory]
    [SupportedOSPlatform("windows")]
    [Trait("Category", "WindowsIntegration")]
    [InlineData("background archive password 🗝")]
    public async Task HeadlessAgentResolvesSavedArchivePasswordAndCreatesExtractableCopy(string password)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var root = Path.Combine(Path.GetTempPath(), "OneCBackup-encrypted-agent-" + Guid.NewGuid());
        var store = new WindowsArchivePasswordStore(); var key = store.SaveNew(password);
        try
        {
            var source = Path.Combine(root, "base"); Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "1Cv8.1CD"), "agent fixture database bytes");
            var job = new BackupJob { Name = "encrypted agent fixture", Source = source,
                DestinationDirectory = Path.Combine(root, "copies"), OneCMethod = OneCBackupMethod.FileArchive,
                ScheduleKind = ScheduleKind.Manual, ArchivePasswordKey = key };
            await new JobStore(Path.Combine(root, "jobs.json")).SaveAsync([job]);
            var host = new LocalAgentHost(root, new DatabaseBackupExecutor(new BackupProcessRunner()), new WindowsFtpCredentialStore());
            Assert.Equal(0, await host.RunAsync(job.Id));
            var run = Assert.Single(await new BackupRunHistory(Path.Combine(root, "runs.json")).ReadAsync());
            Assert.Equal(BackupRunStatus.Succeeded, run.Status);
            var record = Assert.Single(await new BackupCatalog(Path.Combine(root, "copies.json")).ReadAsync());
            Assert.Equal(key, record.Backup.ArchivePasswordKey);
            var restore = Path.Combine(root, "restore"); Directory.CreateDirectory(restore);
            var restored = await new OneCArchiveExtractor().ExtractAsync(record, job, restore);
            Assert.Equal("agent fixture database bytes", await File.ReadAllTextAsync(restored.Path));
            Assert.DoesNotContain(password, await File.ReadAllTextAsync(Path.Combine(root, "runs.json")));
        }
        finally { store.Delete(key); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [WindowsCredentialTheory]
    [SupportedOSPlatform("windows")]
    [Trait("Category", "WindowsIntegration")]
    [InlineData("пароль архива 🗝")]
    public async Task ArchivePasswordsUseDistinctImmutableBindingsAndSeparateNamespace(string password)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var store = new WindowsArchivePasswordStore();
        var first = store.SaveNew(password); string? second = null;
        try
        {
            second = store.SaveNew("changed archive password");
            Assert.NotEqual(first, second);
            Assert.Equal(password, await store.GetAsync(first));
            Assert.Equal("changed archive password", await store.GetAsync(second));
            await Assert.ThrowsAsync<InvalidOperationException>(() => new WindowsFtpCredentialStore().GetAsync(first));
        }
        finally { store.Delete(first); if (second is not null) store.Delete(second); }
    }

    [WindowsCredentialTheory]
    [SupportedOSPlatform("windows")]
    [Trait("Category", "WindowsIntegration")]
    [InlineData("пароль 🗝")]
    [InlineData("")]
    public async Task NativeCredentialRoundTripUpdateAndExplicitCleanup(string password)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var key = "integration-fixture-" + Guid.NewGuid().ToString("N");
        var store = new WindowsFtpCredentialStore();
        try
        {
            store.Save(key, "fixture-user", password);
            var first = await store.GetAsync(key);
            Assert.Equal("fixture-user", first.UserName);
            Assert.Equal(password, first.Password);
            store.Save(key, "updated-user", "updated-password");
            var updated = await store.GetAsync(key);
            Assert.Equal("updated-user", updated.UserName);
            Assert.Equal("updated-password", updated.Password);
            Assert.True(store.Delete(key));
            Assert.False(store.Delete(key));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAsync(key));
        }
        finally { store.Delete(key); }
    }
}
