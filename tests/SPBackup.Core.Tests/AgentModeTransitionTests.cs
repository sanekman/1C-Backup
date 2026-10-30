using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class AgentModeTransitionTests
{
    [Fact]
    public void ManagedUsesSavedEnrollmentInsteadOfStaleExecutionFields()
    {
        var current = new AgentSettings { ServerAddress = "https://old.example/", CredentialKey = "old" };
        var connection = new ServerMonitoringSettings("https://server.example/api-prefix/", current.AgentId, "vault-reference");
        var updated = AgentModeTransition.Prepare(current, AgentMode.Managed, connection, 30);
        Assert.Equal(AgentMode.Managed, updated.Mode);
        Assert.Equal(current.AgentId, updated.AgentId);
        Assert.Equal(connection.Address, updated.ServerAddress);
        Assert.Equal(connection.CredentialKey, updated.CredentialKey);
        Assert.Equal(30, updated.SyncIntervalSeconds);
        Assert.Equal(AgentMode.Standalone, current.Mode);
        Assert.Equal("old", current.CredentialKey);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("agent")]
    [InlineData("https")]
    [InlineData("key")]
    public void UnusableEnrollmentCannotEnableManaged(string fault)
    {
        var current = new AgentSettings();
        ServerMonitoringSettings? connection = new("https://server.example/", current.AgentId, "key");
        connection = fault switch {
            "missing" => null, "agent" => connection with { AgentId = Guid.NewGuid() },
            "https" => connection with { Address = "http://server.example/" },
            _ => connection with { CredentialKey = "bad\nkey" } };
        var error = Record.Exception(() => AgentModeTransition.Prepare(current, AgentMode.Managed, connection));
        Assert.True(error is InvalidDataException or InvalidOperationException);
        Assert.Equal(AgentMode.Standalone, current.Mode);
        Assert.Empty(current.CredentialKey);
    }

    [Theory]
    [InlineData(14)]
    [InlineData(86401)]
    public void InvalidIntervalDoesNotProduceSettings(int interval)
    {
        var current = new AgentSettings();
        Assert.Throws<InvalidDataException>(() => AgentModeTransition.Prepare(current, AgentMode.Standalone, null, interval));
        Assert.Equal(60, current.SyncIntervalSeconds);
    }

    [Fact]
    public async Task ReturningToLocalPreservesLocalJobsAndServerCacheAndAllowsBrokenEnrollment()
    {
        var root = Path.Combine(Path.GetTempPath(), "sp-mode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var original = new AgentSettings();
            var connection = new ServerMonitoringSettings("https://server.example/", original.AgentId, "key");
            var managed = AgentModeTransition.Prepare(original, AgentMode.Managed, connection);
            var profile = new ManagedAgentProfile(root, managed, connection);
            var local = new BackupJob { Name = "Keep local job", Kind = DatabaseKind.FilesAndFolders,
                Source = Path.Combine(root, "source"), DestinationDirectory = Path.Combine(root, "copies") };
            var store = new JobStore(Path.Combine(root, "jobs.json"));
            await store.SaveAsync([local]);
            await new AgentConfigurationCache(profile.CachePath).SaveAsync(new(managed.NormalizedServerAddress,
                new(managed.AgentId, 5, [local with { Name = "Server job with same ID" }]), DateTimeOffset.UtcNow));
            var cacheBefore = await File.ReadAllBytesAsync(profile.CachePath);
            var localBefore = await File.ReadAllBytesAsync(Path.Combine(root, "jobs.json"));
            var restored = AgentModeTransition.Prepare(managed, AgentMode.Standalone, connection with { Address = "broken" });
            await new AgentSettingsStore(Path.Combine(root, "agent.json")).SaveAsync(restored);
            Assert.Equal(original, await new AgentSettingsStore(Path.Combine(root, "agent.json")).LoadAsync());
            Assert.Equal(localBefore, await File.ReadAllBytesAsync(Path.Combine(root, "jobs.json")));
            Assert.Equal(cacheBefore, await File.ReadAllBytesAsync(profile.CachePath));
            Assert.Equal("Keep local job", Assert.Single(await store.LoadAsync()).Name);
        }
        finally { Directory.Delete(root, true); }
    }
}
