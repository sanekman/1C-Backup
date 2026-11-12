using Npgsql;
using SPBackup.Server;
using Xunit;

namespace SPBackup.Server.Tests;

public sealed class FolderMigrationTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    [ServerFact]
    public async Task SchemaFourUpgradePreservesNamesTokensConfigurationsAndRuns()
    {
        var a = await fixture.EnrollAsync();
        var run = new SPBackup.Server.Monitoring.MonitoredRun(Guid.NewGuid(), Guid.NewGuid(), "History", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, "Succeeded", 0, false, "Full", []);
        await fixture.Store.SaveRunAsync(a.AgentId, run, default);
        var config = await fixture.Store.ConfigurationAsync(a.AgentId, default);
        await fixture.RemoveFolderSchemaForMigrationTestAsync();
        Assert.False(await fixture.Store.ReadyAsync());
        await fixture.Store.MigrateAsync(); await fixture.Store.MigrateAsync();
        Assert.True(await fixture.Store.ReadyAsync());
        var agent = (await fixture.Store.AgentAsync(a.AgentId, default))!;
        Assert.Equal("Test agent", agent.Name); Assert.Equal(agent.Name, agent.ComputerName); Assert.Null(agent.FolderId);
        Assert.Equal(a.AgentId, await fixture.Store.AuthenticateAgentAsync(a.Token, default));
        Assert.Equal(config, await fixture.Store.ConfigurationAsync(a.AgentId, default));
        Assert.Equal(run.RunId, Assert.Single((await fixture.Store.RunsAsync(a.AgentId, 0, default)).Runs).RunId);
    }
    [ServerFact]
    public async Task FailedMigrationRollsBackColumnsAndTreeAndCanBeRetried()
    {
        var agent = await fixture.EnrollAsync();
        await fixture.RemoveFolderSchemaForMigrationTestAsync();
        await fixture.ExecuteAsync("CREATE FUNCTION fail_schema_five() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.version=5 THEN RAISE EXCEPTION 'fixture'; END IF; RETURN NEW; END $$; CREATE TRIGGER fail_schema_five BEFORE INSERT ON server_schema FOR EACH ROW EXECUTE FUNCTION fail_schema_five()");
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => fixture.Store.MigrateAsync());
            await using var db = new NpgsqlConnection(File.ReadAllText(Path.Combine(fixture.Root, "connection.txt"))); await db.OpenAsync();
            await using var query = new NpgsqlCommand("SELECT max(version)=4 AND to_regclass('server_agent_folders') IS NULL FROM server_schema", db);
            Assert.True((bool)(await query.ExecuteScalarAsync())!);
            Assert.Equal(agent.AgentId, await fixture.Store.AuthenticateAgentAsync(agent.Token, default));
        }
        finally
        {
            await fixture.ExecuteAsync("DROP TRIGGER fail_schema_five ON server_schema; DROP FUNCTION fail_schema_five()");
            await fixture.Store.MigrateAsync();
        }
        Assert.Equal("Test agent", (await fixture.Store.AgentAsync(agent.AgentId, default))!.ComputerName);
    }
    [Fact]
    public void TreeValidationBoundsDepthCountAndRejectsDisconnectedCycles()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        Assert.Throws<ServerRequestException>(() => ServerStore.FolderOptions([new(a, b, "A"), new(b, a, "B")]));
        var excessive = Enumerable.Range(0, 1001).Select(i => new AgentFolder(Guid.NewGuid(), null, i.ToString())).ToArray();
        Assert.Equal("folder_limit", Assert.Throws<ServerRequestException>(() => ServerStore.FolderOptions(excessive)).Code);
    }
}
