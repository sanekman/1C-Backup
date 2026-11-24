using System.Text.Json;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class ProtectedPostgreSqlJobTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "SPBackup-tls-store-" + Guid.NewGuid());
    private string PathToJobs => Path.Combine(root, "jobs.json");
    private BackupJob Ordinary => new() { Name = "ordinary", Source = "database", DestinationDirectory = Path.Combine(root, "copies") };
    private BackupJob Secure => Ordinary with { Kind = DatabaseKind.PostgreSql, Name = "protected", PostgreSql = new()
    { PgDumpPath = Path.Combine(root, "pg_dump"), PgRestorePath = Path.Combine(root, "pg_restore"), Tls = new() } };

    [Fact]
    public async Task LegacyFileContainsOnlyOrdinaryJobsAndLegacyEditDoesNotEraseProtectedJob()
    {
        var store = new JobStore(PathToJobs); var ordinary = Ordinary; var secure = Secure;
        await store.SaveAsync([ordinary, secure]);
        using (var legacy = JsonDocument.Parse(await File.ReadAllTextAsync(PathToJobs)))
        {
            Assert.Equal(1, legacy.RootElement.GetArrayLength());
            Assert.Equal(ordinary.Id, legacy.RootElement[0].GetProperty("Id").GetGuid());
        }
        // An older application edits only the file it knows.
        await File.WriteAllTextAsync(PathToJobs, JsonSerializer.Serialize(new[] { ordinary with { Name = "edited by old application" } }));
        var loaded = await store.LoadAsync();
        Assert.Equal(2, loaded.Count);
        Assert.NotNull(Assert.Single(loaded, job => job.Id == secure.Id).PostgreSql!.Tls);
        Assert.Equal("edited by old application", Assert.Single(loaded, job => job.Id == ordinary.Id).Name);
    }

    [Fact]
    public async Task ChangingProtectedJobRequiresIdleEngineButOrdinaryEditDoesNot()
    {
        var store = new JobStore(PathToJobs); var ordinary = Ordinary; var secure = Secure;
        await store.SaveAsync([ordinary, secure]);
        using var active = new BackupEngineCoordinator(root).Acquire();
        await Assert.ThrowsAsync<BackupEngineBusyException>(() => store.SaveAsync([ordinary, secure with { Name = "changed" }]));
        await store.SaveAsync([ordinary with { Name = "edited" }, secure]);
        Assert.Equal(secure.Name, Assert.Single(await store.LoadAsync(), job => job.Id == secure.Id).Name);
    }

    [Fact]
    public async Task DurablePendingSnapshotRecoversWholeConfigurationAfterInterruptedPublication()
    {
        var store = new JobStore(PathToJobs); var secure = Secure; var ordinary = Ordinary;
        await store.SaveAsync([ordinary]);
        await File.WriteAllTextAsync(PathToJobs + ".pending.json", JsonSerializer.Serialize(new
        { FormatVersion = 1, Jobs = new[] { ordinary with { Name = "committed ordinary" }, secure } }));
        var loaded = await store.LoadAsync();
        Assert.Equal(2, loaded.Count);
        Assert.Equal("committed ordinary", Assert.Single(loaded, job => job.Id == ordinary.Id).Name);
        Assert.NotNull(Assert.Single(loaded, job => job.Id == secure.Id).PostgreSql!.Tls);
        await store.SaveAsync(loaded);
        Assert.False(File.Exists(PathToJobs + ".pending.json"));
        using var legacy = JsonDocument.Parse(await File.ReadAllTextAsync(PathToJobs));
        Assert.Equal(1, legacy.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task CorruptProtectedConfigurationNeverFallsBackToLegacy()
    {
        var store = new JobStore(PathToJobs); await store.SaveAsync([Ordinary, Secure]);
        await File.WriteAllTextAsync(PathToJobs + ".protected.json", "null");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
    }

    [Fact]
    public async Task ExplicitTlsAlsoAppliesWithoutSavedPassword()
    {
        using var connection = await PostgreSqlConnectionEnvironment.OpenAsync(Secure, null, CancellationToken.None);
        Assert.Equal("verify-full", connection.Environment!["PGSSLMODE"]);
        Assert.Null(connection.Environment["PGSERVICE"]);
        Assert.Null(connection.Environment["PGHOSTADDR"]);
        Assert.False(connection.Environment.ContainsKey("PGPASSFILE"));
    }

    [Fact]
    public async Task UnknownProtectedSecurityOptionCannotBeSilentlyIgnored()
    {
        var store = new JobStore(PathToJobs); await store.SaveAsync([Ordinary, Secure]);
        var path = PathToJobs + ".protected.json";
        var data = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        data["Jobs"]![0]!["PostgreSql"]!["Tls"]!["UnknownRequiredPolicy"] = true;
        await File.WriteAllTextAsync(path, data.ToJsonString());
        await Assert.ThrowsAsync<JsonException>(() => store.LoadAsync());
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
