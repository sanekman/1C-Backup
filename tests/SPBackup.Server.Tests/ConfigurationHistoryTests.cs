using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Npgsql;
using SPBackup.Server;
using Xunit;

namespace SPBackup.Server.Tests;

public sealed class ConfigurationHistoryTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private static string Url(Guid id) => $"api/v1/management/agents/{id}/configuration";
    private static ManagedFileJob Job() => new() { Id = Guid.NewGuid(), Name = "Saved company", Source = @"C:\Bases\Company",
        DestinationDirectory = @"D:\Copies", Enabled = true, ScheduleKind = "Weekly", DaysOfWeek = "Tuesday, Saturday",
        DailyAt = new TimeOnly(13, 45, 12).Add(TimeSpan.FromTicks(1234567)), AdditionalDailyTimes = [new(23, 15)],
        FileBackupMode = "Incremental", FullBackupDayOfWeek = DayOfWeek.Sunday,
        AdditionalDestinations = [new() { StorageKind = "NetworkShare", DestinationDirectory = @"\\storage\copies", KeepLast = 30 }] };
    private async Task<ServerUser> User() => (await fixture.Store.LoginAsync("administrator", fixture.Password, default))!;
    private async Task<ManagedFileConfiguration> Publish(Guid id, long expected, params ManagedFileJob[] jobs)
    {
        var user = await User();
        return await fixture.Store.PublishFileConfigurationAsync(user.Id, user.SecurityStamp, id, expected, new(id, 1, jobs), default);
    }
    private async Task<HttpResponseMessage> Restore(Guid id, long revision, string? expected, HttpClient? browser = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url(id) + $"/history/{revision}/restore");
        if (expected is not null) request.Headers.TryAddWithoutValidation("If-Match", expected);
        return await (browser ?? fixture.Administrator).SendAsync(request);
    }
    private async Task<int> RestoredAudits(Guid id) => (await fixture.Store.AuditPageAsync(0)).Entries.Count(e => e.AgentId == id && e.Event == "ConfigurationRestored");

    [ServerFact]
    public async Task RestorePublishesTheExactHistoricalPlanAsANewRevisionAndSurvivesRestart()
    {
        var agent = await fixture.EnrollAsync(); var job = Job(); await Publish(agent.AgentId, 1, job);
        var old = await fixture.Store.ReadFileConfigurationRevisionAsync(agent.AgentId, 2, default);
        await Publish(agent.AgentId, 2); // Deleted from the active plan.
        var run = new SPBackup.Server.Monitoring.MonitoredRun(Guid.NewGuid(), job.Id, "History preserved", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, "Succeeded", 0, false, "Full", []);
        await fixture.Store.SaveRunAsync(agent.AgentId, run, default);
        using (var response = await Restore(agent.AgentId, 2, "\"3\""))
        { Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("\"4\"", response.Headers.ETag!.Tag); }
        var current = await fixture.Store.ReadFileConfigurationAsync(agent.AgentId, default);
        Assert.Equal(4, current.Revision); Assert.Equal(JsonSerializer.Serialize(old.Jobs), JsonSerializer.Serialize(current.Jobs));
        Assert.Equal(JsonSerializer.Serialize(old), JsonSerializer.Serialize(await fixture.Store.ReadFileConfigurationRevisionAsync(agent.AgentId, 2, default)));
        Assert.Empty((await fixture.Store.ReadFileConfigurationRevisionAsync(agent.AgentId, 3, default)).Jobs);
        var history = await fixture.Store.FileConfigurationHistoryAsync(agent.AgentId, 0, default);
        Assert.Equal(4, history.Total); Assert.Equal(2, history.Entries[0].RestoredFrom); Assert.Equal("administrator", history.Entries[0].Author);
        using (var response = await fixture.Administrator.GetAsync(Url(agent.AgentId) + "/history"))
        { response.EnsureSuccessStatusCode(); Assert.True(response.Headers.CacheControl!.NoStore); Assert.Contains("restoredFrom", await response.Content.ReadAsStringAsync()); }
        using (var response = await fixture.Administrator.GetAsync(Url(agent.AgentId) + "/history/2"))
        {
            response.EnsureSuccessStatusCode(); Assert.Equal("\"2\"", response.Headers.ETag!.Tag);
            Assert.Equal(JsonSerializer.Serialize(old), JsonSerializer.Serialize(JsonSerializer.Deserialize<ManagedFileConfiguration>(await response.Content.ReadAsStringAsync(), Json)));
        }
        Assert.Equal(1, history.Entries[0].JobCount); Assert.Null((await fixture.Store.AgentAsync(agent.AgentId, default))!.ReportedRevision);
        using var client = fixture.Client(); client.DefaultRequestHeaders.Authorization = new("Bearer", agent.Token);
        client.DefaultRequestHeaders.Add(SPBackup.Server.AgentCapabilities.Header, "configuration-v1,strict-configuration-v1,managed-execution-v1");
        using var offered = await client.GetAsync($"api/v1/agents/{agent.AgentId}/configuration"); Assert.Equal(HttpStatusCode.OK, offered.StatusCode);
        var wire = JsonSerializer.Deserialize<SPBackup.Core.ServerConfiguration>(await offered.Content.ReadAsStringAsync(), Json)!;
        Assert.Equal(4, wire.Revision); Assert.Equal(job.Id, Assert.Single(wire.Jobs).Id);
        if (OperatingSystem.IsWindows()) Assert.Empty(wire.Jobs[0].Validate());
        Assert.Single((await fixture.Store.RunsAsync(agent.AgentId, 0, default)).Runs, r => r.RunId == run.RunId);
        await fixture.RestartAsync();
        Assert.Equal(4, (await fixture.Store.FileConfigurationHistoryAsync(agent.AgentId, 0, default)).Total);
        Assert.Equal(1, await RestoredAudits(agent.AgentId));
    }

    [ServerFact]
    public async Task ConcurrentRestoreRequestsAndRepeatedPostsCannotCreateTwoNewRevisions()
    {
        var agent = await fixture.EnrollAsync(); await Publish(agent.AgentId, 1, Job()); await Publish(agent.AgentId, 2);
        var responses = await Task.WhenAll(Restore(agent.AgentId, 2, "\"3\""), Restore(agent.AgentId, 1, "\"3\""));
        try { Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK); Assert.Single(responses, r => r.StatusCode == HttpStatusCode.PreconditionFailed); }
        finally { foreach (var r in responses) r.Dispose(); }
        using var repeat = await Restore(agent.AgentId, 2, "\"3\""); Assert.Equal(HttpStatusCode.PreconditionFailed, repeat.StatusCode);
        Assert.Equal(4, (await fixture.Store.ConfigurationAsync(agent.AgentId, default)).Revision);
        Assert.Equal(4, (await fixture.Store.FileConfigurationHistoryAsync(agent.AgentId, 0, default)).Total);
        Assert.Equal(1, await RestoredAudits(agent.AgentId));
    }

    [ServerTheory]
    [InlineData("server_configuration_history")]
    [InlineData("server_audit")]
    public async Task HistoryOrAuditWriteFailureRollsBackTheActivePlanAndEveryHistoryRow(string table)
    {
        var agent = await fixture.EnrollAsync(); await Publish(agent.AgentId, 1, Job()); var before = await fixture.Store.ConfigurationAsync(agent.AgentId, default);
        var fail = table == "server_configuration_history" ? "IF NEW.revision>2 THEN RAISE EXCEPTION 'fixture'; END IF; RETURN NEW;" : "RAISE EXCEPTION 'fixture';";
        await fixture.ExecuteAsync($"CREATE FUNCTION fail_history_write() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN {fail} END $$; CREATE TRIGGER fail_history_write BEFORE INSERT ON {table} FOR EACH ROW EXECUTE FUNCTION fail_history_write()");
        try
        {
            var user = await User();
            await Assert.ThrowsAsync<PostgresException>(() => fixture.Store.RestoreFileConfigurationAsync(user.Id, user.SecurityStamp, agent.AgentId, 2, 1, default));
            Assert.Equal(before, await fixture.Store.ConfigurationAsync(agent.AgentId, default));
            Assert.Equal(2, (await fixture.Store.FileConfigurationHistoryAsync(agent.AgentId, 0, default)).Total);
            Assert.Equal(0, await RestoredAudits(agent.AgentId));
        }
        finally { await fixture.ExecuteAsync($"DROP TRIGGER fail_history_write ON {table}; DROP FUNCTION fail_history_write()"); }
    }

    [ServerFact]
    public async Task RetentionKeepsOneHundredPlansPerAgentAndAnExpiredPlanCannotBeRestored()
    {
        var agent = await fixture.EnrollAsync(); var other = await fixture.EnrollAsync();
        var user = await User();
        for (long revision = 1; revision <= 103; revision++)
            await fixture.Store.PublishFileConfigurationAsync(user.Id, user.SecurityStamp, agent.AgentId, revision, new(agent.AgentId, 1, []), default);
        var first = await fixture.Store.FileConfigurationHistoryAsync(agent.AgentId, -10, default);
        var last = await fixture.Store.FileConfigurationHistoryAsync(agent.AgentId, 10000, default);
        Assert.Equal(100, first.Total); Assert.Equal(20, first.Entries.Count); Assert.Equal(104, first.Entries[0].Revision);
        Assert.Equal(4, last.Page); Assert.Equal(5, last.Entries[^1].Revision);
        Assert.Single((await fixture.Store.FileConfigurationHistoryAsync(other.AgentId, 0, default)).Entries);
        using var expired = await Restore(agent.AgentId, 4, "\"104\""); Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
        Assert.Equal(104, (await fixture.Store.ConfigurationAsync(agent.AgentId, default)).Revision);
    }

    [ServerFact]
    public async Task SchemaThreeUpgradeSeedsOnlyTheExistingPlanWithoutInventingOldHistory()
    {
        var agent = await fixture.EnrollAsync(); await Publish(agent.AgentId, 1, Job());
        var before = await fixture.Store.ConfigurationAsync(agent.AgentId, default);
        await fixture.ExecuteAsync("DROP TABLE server_configuration_history; DELETE FROM server_schema WHERE version=4");
        Assert.False(await fixture.Store.ReadyAsync()); await fixture.Store.MigrateAsync(); await fixture.Store.MigrateAsync();
        Assert.True(await fixture.Store.ReadyAsync()); Assert.Equal(before, await fixture.Store.ConfigurationAsync(agent.AgentId, default));
        var history = await fixture.Store.FileConfigurationHistoryAsync(agent.AgentId, 0, default);
        var seed = Assert.Single(history.Entries); Assert.Equal(2, seed.Revision); Assert.Null(seed.Author); Assert.Null(seed.RestoredFrom);
        Assert.Equal(agent.AgentId, await fixture.Store.AuthenticateAgentAsync(agent.Token, default));
    }

    [ServerFact]
    public async Task FailedSchemaFourMigrationPreservesSchemaThreeAndCanBeRetried()
    {
        var agent = await fixture.EnrollAsync(); var before = await fixture.Store.ConfigurationAsync(agent.AgentId, default);
        await fixture.ExecuteAsync("DROP TABLE server_configuration_history; DELETE FROM server_schema WHERE version=4; CREATE FUNCTION fail_schema_four() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.version=4 THEN RAISE EXCEPTION 'fixture'; END IF; RETURN NEW; END $$; CREATE TRIGGER fail_schema_four BEFORE INSERT ON server_schema FOR EACH ROW EXECUTE FUNCTION fail_schema_four()");
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => fixture.Store.MigrateAsync());
            await using var db = new NpgsqlConnection(File.ReadAllText(Path.Combine(fixture.Root, "connection.txt"))); await db.OpenAsync();
            await using var query = new NpgsqlCommand("SELECT max(version)=3 AND to_regclass('server_configuration_history') IS NULL FROM server_schema", db);
            Assert.Equal(true, await query.ExecuteScalarAsync()); Assert.Equal(before, await fixture.Store.ConfigurationAsync(agent.AgentId, default));
        }
        finally { await fixture.ExecuteAsync("DROP TRIGGER fail_schema_four ON server_schema; DROP FUNCTION fail_schema_four()"); await fixture.Store.MigrateAsync(); }
    }

    [ServerFact]
    public async Task RestoreRejectsUntrustedSessionsMissingPreconditionsInvalidDocumentsAndRevokedAgents()
    {
        var agent = await fixture.EnrollAsync(); await Publish(agent.AgentId, 1, Job()); var before = await fixture.Store.ConfigurationAsync(agent.AgentId, default);
        using var token = fixture.Client(); token.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", agent.Token);
        using (var denied = await token.GetAsync(Url(agent.AgentId) + "/history")) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using (var denied = await Restore(agent.AgentId, 1, "\"2\"", token)) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var noCsrf = fixture.Client(); await fixture.SignInAsync(noCsrf);
        using (var denied = await Restore(agent.AgentId, 1, "\"2\"", noCsrf)) Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        using (var missing = await Restore(agent.AgentId, 1, null)) Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        using (var invalid = await Restore(agent.AgentId, 1, "2")) Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using (var current = await Restore(agent.AgentId, 2, "\"2\"")) Assert.Equal(HttpStatusCode.Conflict, current.StatusCode);
        await fixture.ExecuteAsync("UPDATE server_configuration_history SET document=jsonb_set(document,'{unsupported}','true') WHERE agent_id=@id AND revision=1", ("id", agent.AgentId));
        using (var unsupported = await Restore(agent.AgentId, 1, "\"2\"")) Assert.Equal(HttpStatusCode.Conflict, unsupported.StatusCode);
        var other = await fixture.EnrollAsync();
        using (var foreign = await Restore(other.AgentId, 2, "\"1\"")) Assert.Equal(HttpStatusCode.Conflict, foreign.StatusCode);
        using (var revoked = await fixture.Administrator.PostAsync($"api/v1/management/agents/{agent.AgentId}/revoke", ServerFixture.Json(new { }))) revoked.EnsureSuccessStatusCode();
        using (var denied = await Restore(agent.AgentId, 1, "\"2\"")) Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        var user = await User();
        var stale = await Assert.ThrowsAsync<ServerRequestException>(() => fixture.Store.RestoreFileConfigurationAsync(user.Id, Guid.NewGuid(), other.AgentId, 1, 1, default));
        Assert.Equal("invalid_session", stale.Code);
        Assert.Equal(before, await fixture.Store.ConfigurationAsync(agent.AgentId, default));
        Assert.Equal(0, await RestoredAudits(agent.AgentId));
    }
}
