using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using SPBackup.Core;
using SPBackup.Server;
using Xunit;
using Caps = SPBackup.Server.AgentCapabilities;
using Receipt = SPBackup.Server.ConfigurationReceipt;

namespace SPBackup.Server.Tests;

public sealed class ConfigurationReceiptTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private HttpClient Agent(EnrollmentResponse agent, bool receipts = true)
    {
        var client = fixture.Client(); client.DefaultRequestHeaders.Authorization = new("Bearer", agent.Token);
        if (receipts) client.DefaultRequestHeaders.Add(Caps.Header, Caps.HeaderValue);
        return client;
    }
    private async Task<Guid> Offer(EnrollmentResponse agent, long? conditional = null)
    {
        using var client = Agent(agent);
        if (conditional is not null) client.DefaultRequestHeaders.IfNoneMatch.Add(new($"\"{conditional}\""));
        using var response = await client.GetAsync($"api/v1/agents/{agent.AgentId}/configuration");
        Assert.Equal(conditional is null ? HttpStatusCode.OK : HttpStatusCode.NotModified, response.StatusCode);
        return Guid.Parse(Assert.Single(response.Headers.GetValues(Receipt.OfferHeader)));
    }
    private async Task Version(Guid agent, long revision, bool unsupported = false)
    {
        var doc = JsonSerializer.SerializeToNode(new { agentId = agent, revision, protocolVersion = 1, jobs = Array.Empty<object>() })!;
        if (unsupported) doc["futureSecurity"] = true;
        await fixture.ExecuteAsync("UPDATE server_configurations SET revision=@revision,document=@document::jsonb WHERE agent_id=@id",
            ("revision", revision), ("document", doc.ToJsonString()), ("id", agent));
    }
    private static Receipt Decision(EnrollmentResponse a, Guid offer, long rev = 1, string status = "Accepted", string? reason = null) => new(a.AgentId, rev, offer, status, reason, DateTimeOffset.UtcNow);
    private async Task<HttpResponseMessage> Post(EnrollmentResponse agent, Receipt receipt)
    { using var client = Agent(agent); return await client.PostAsync($"api/v1/agents/{agent.AgentId}/configuration-receipt", ServerFixture.Json(receipt)); }
    private Task<ConfigurationDecisionState> State(EnrollmentResponse a) => fixture.Store.ConfigurationDecisionsAsync(a.AgentId, default);
    private sealed class Credentials(string token) : IAgentCredentialProvider
    { public Task<string> GetBearerTokenAsync(string key, CancellationToken token2 = default) => Task.FromResult(token); }

    [ServerFact]
    public async Task LegacyRequestDoesNotCreateOffersAndNegotiatedOfferIsStableFor304()
    {
        var agent = await fixture.EnrollAsync(); using var legacy = Agent(agent, false);
        using var old = await legacy.GetAsync($"api/v1/agents/{agent.AgentId}/configuration");
        Assert.Equal(HttpStatusCode.OK, old.StatusCode); Assert.False(old.Headers.Contains(Receipt.OfferHeader)); Assert.Null((await State(agent)).Current);
        var id = await Offer(agent); Assert.Equal(id, await Offer(agent, 1));
        Assert.Equal("Awaiting", (await State(agent)).Current!.Status); Assert.Null((await State(agent)).LastAccepted);
    }

    [ServerFact]
    public async Task RealClientRejectsNewOfferWithoutRejectingThePreviousCache()
    {
        var agent = await fixture.EnrollAsync(); using var api = new AgentApiClient(new Credentials(agent.Token), fixture.Handler());
        var settings = new AgentSettings { Mode = AgentMode.Managed, AgentId = agent.AgentId, ServerAddress = fixture.Address.ToString(), CredentialKey = "test" };
        var cachePath = Path.Combine(fixture.Root, Guid.NewGuid()+".json");
        var sync = new AgentConfiguration(new JobStore(Path.Combine(fixture.Root, Guid.NewGuid()+".json")), new(cachePath), api);
        Assert.True((await sync.SynchronizeAsync(settings, DateTimeOffset.UtcNow)).Succeeded);
        var previous = await File.ReadAllBytesAsync(cachePath);
        await Version(agent.AgentId, 2, true);
        var rejected = await sync.SynchronizeAsync(settings, DateTimeOffset.UtcNow);
        Assert.False(rejected.Succeeded); Assert.False(rejected.ReceiptPending); Assert.Equal(1, rejected.Revision);
        Assert.Equal(previous, await File.ReadAllBytesAsync(cachePath));
        var state = await State(agent);
        Assert.Equal(2, state.Current!.Revision); Assert.Equal("Rejected", state.Current.Status); Assert.Equal("InvalidConfiguration", state.Current.Reason);
        Assert.Equal(1, state.LastAccepted!.Revision); Assert.Null((await fixture.Store.AgentAsync(agent.AgentId, default))!.ReportedRevision);
        await fixture.RestartAsync(); Assert.Equal(state, await State(agent));
        using var panel = await fixture.Administrator.GetAsync($"agents/{agent.AgentId}");
        Assert.Contains("Агент отклонил настройки", WebUtility.HtmlDecode(await panel.Content.ReadAsStringAsync()));
    }

    [ServerFact]
    public async Task OutOfOrderAndDuplicateDecisionsCannotRegressAcceptance()
    {
        var agent = await fixture.EnrollAsync(); var one = await Offer(agent); await Version(agent.AgentId, 2); var two = await Offer(agent);
        using (var accepted = await Post(agent, Decision(agent, two, 2))) Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        var first = (await State(agent)).Current;
        using (var late = await Post(agent, Decision(agent, one))) Assert.Equal(HttpStatusCode.NoContent, late.StatusCode);
        using (var retry = await Post(agent, Decision(agent, two, 2) with { ObservedUtc = DateTimeOffset.UtcNow.AddDays(1) })) Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        using (var delayedRejection = await Post(agent, Decision(agent, two, 2, "Rejected", "CacheWriteFailed"))) Assert.Equal(HttpStatusCode.NoContent, delayedRejection.StatusCode);
        var state = await State(agent); Assert.Equal(first, state.Current); Assert.Equal(2, state.LastAccepted!.Revision);
        Assert.Equal(2, (await fixture.Store.AuditPageAsync(0)).Entries.Count(e => e.AgentId == agent.AgentId && e.Event == "ConfigurationAccepted"));
        Assert.DoesNotContain((await fixture.Store.AuditPageAsync(0)).Entries, e => e.AgentId == agent.AgentId && e.Event == "ConfigurationRejected");
    }

    [ServerFact]
    public async Task FailedCacheCanBeAcceptedAfterRecoveryWithoutDuplicateRejectionEvents()
    {
        var agent = await fixture.EnrollAsync(); var id = await Offer(agent);
        using (var first = await Post(agent, Decision(agent, id, 1, "Rejected", "CacheWriteFailed"))) first.EnsureSuccessStatusCode();
        var rejected = (await State(agent)).Current;
        using (var repeat = await Post(agent, Decision(agent, id, 1, "Rejected", "InvalidConfiguration"))) repeat.EnsureSuccessStatusCode();
        Assert.Equal(rejected, (await State(agent)).Current);
        using (var repaired = await Post(agent, Decision(agent, id))) repaired.EnsureSuccessStatusCode();
        Assert.Equal("Accepted", (await State(agent)).Current!.Status); Assert.Null((await State(agent)).Current!.Reason);
        Assert.Equal(1, (await fixture.Store.AuditPageAsync(0)).Entries.Count(e => e.AgentId == agent.AgentId && e.Event == "ConfigurationRejected"));
        Assert.Equal(1, (await fixture.Store.AuditPageAsync(0)).Entries.Count(e => e.AgentId == agent.AgentId && e.Event == "ConfigurationAccepted"));
    }

    [ServerFact]
    public async Task UnknownForeignAndMalformedReceiptsCannotAcknowledgeSettings()
    {
        var a = await fixture.EnrollAsync(); var b = await fixture.EnrollAsync(); var id = await Offer(a);
        using (var foreign = await Post(b, Decision(b, id))) Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using (var mismatch = await Post(a, Decision(b, id))) Assert.Equal(HttpStatusCode.Forbidden, mismatch.StatusCode);
        using (var unknown = await Post(a, Decision(a, Guid.NewGuid()))) Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using (var revision = await Post(a, Decision(a, id, 2))) Assert.Equal(HttpStatusCode.Conflict, revision.StatusCode);
        foreach (var bad in new[] { Decision(a,id,1,"Accepted","PRIVATE_PASSWORD"), Decision(a,id,1,"Rejected",null), Decision(a,id,1,"Unsupported",null), Decision(a,id) with {ObservedUtc=default} })
        {
            using var invalid = await Post(a, bad); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.DoesNotContain("PRIVATE", await invalid.Content.ReadAsStringAsync());
        }
        using var client = Agent(a);
        var json = JsonSerializer.Serialize(Decision(a, id), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var bad in new[] { json.Replace("\"status\":", "\"password\":\"PRIVATE\",\"status\":"), json.Replace("\"status\":", "\"status\":\"Rejected\",\"status\":") })
        {
            using var response = await client.PostAsync($"api/v1/agents/{a.AgentId}/configuration-receipt", new StringContent(bad, System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Equal("Awaiting", (await State(a)).Current!.Status); Assert.Null((await State(a)).LastAccepted);
    }

    [ServerFact]
    public async Task NewEnrollmentCannotInheritOrUseTheOldConnectionDecision()
    {
        var old = await fixture.EnrollAsync(); var first = await Offer(old);
        using (var accepted = await Post(old, Decision(old, first))) accepted.EnsureSuccessStatusCode();
        using (var revoke = await fixture.Administrator.PostAsync($"api/v1/management/agents/{old.AgentId}/revoke", ServerFixture.Json(new { }))) revoke.EnsureSuccessStatusCode();
        var next = await fixture.EnrollAsync(old.AgentId);
        Assert.Null((await State(next)).Current); Assert.Null((await State(next)).LastAccepted);
        using (var denied = await Post(old, Decision(old, first))) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using (var expired = await Post(next, Decision(next, first))) Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
        var config = await fixture.Store.ConfigurationAsync(next.AgentId, default);
        var stale = await Assert.ThrowsAsync<ServerRequestException>(() => fixture.Store.RegisterConfigurationOfferAsync(next.AgentId, config.Revision, config.Document, ServerTokens.Hash(old.Token), default));
        Assert.Equal(403, stale.Status);
        var second = await Offer(next, 1); Assert.NotEqual(first, second);
        using var acceptedNew = await Post(next, Decision(next, second)); acceptedNew.EnsureSuccessStatusCode(); Assert.Equal(second, (await State(next)).LastAccepted!.OfferId);
    }

    [ServerFact]
    public async Task LegacyHeartbeatAndAgentTokenCannotImpersonateAdministrativeState()
    {
        var a = await fixture.EnrollAsync(); using var client = Agent(a, false);
        using (var heartbeat = await client.PostAsync($"api/v1/agents/{a.AgentId}/heartbeat", ServerFixture.Json(new { agentId=a.AgentId, agentVersion="test", backupExecutionAvailable=true, configurationRevision=1, sentUtc=DateTimeOffset.UtcNow }))) heartbeat.EnsureSuccessStatusCode();
        Assert.Null((await State(a)).LastAccepted);
        using var denied = await client.GetAsync($"api/v1/management/agents/{a.AgentId}/configuration-state"); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var allowed = await fixture.Administrator.GetAsync($"api/v1/management/agents/{a.AgentId}/configuration-state"); Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [ServerFact]
    public async Task AuditFailureRollsBackReceiptUntilRetry()
    {
        var a = await fixture.EnrollAsync(); var id = await Offer(a);
        await fixture.ExecuteAsync("CREATE FUNCTION fail_receipt_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.event='ConfigurationAccepted' THEN RAISE EXCEPTION 'fixture'; END IF; RETURN NEW; END $$; CREATE TRIGGER fail_receipt_audit BEFORE INSERT ON server_audit FOR EACH ROW EXECUTE FUNCTION fail_receipt_audit()");
        try
        {
            using var response = await Post(a, Decision(a, id)); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("Awaiting", (await State(a)).Current!.Status); Assert.Null((await State(a)).LastAccepted);
        }
        finally { await fixture.ExecuteAsync("DROP TRIGGER fail_receipt_audit ON server_audit; DROP FUNCTION fail_receipt_audit()"); }
        using var retry = await Post(a, Decision(a, id)); retry.EnsureSuccessStatusCode(); Assert.Equal("Accepted", (await State(a)).Current!.Status);
    }

    [ServerFact]
    public async Task ConcurrentRejectionAndAcceptanceAlwaysRetainAcceptance()
    {
        var a = await fixture.EnrollAsync(); var id = await Offer(a);
        var results = await Task.WhenAll(Post(a,Decision(a,id,1,"Rejected","InvalidConfiguration")),Post(a,Decision(a,id)));
        try { Assert.All(results,r=>Assert.Equal(HttpStatusCode.NoContent,r.StatusCode)); }
        finally { foreach(var response in results)response.Dispose(); }
        Assert.Equal("Accepted",(await State(a)).Current!.Status);
    }

    [ServerFact]
    public async Task ChangingContentWithoutRevisionCannotReuseAnOfferForNewContent()
    {
        var a=await fixture.EnrollAsync(); var id=await Offer(a); await Version(a.AgentId,1,true);
        using var client=Agent(a); client.DefaultRequestHeaders.IfNoneMatch.Add(new("\"1\""));
        using var changed=await client.GetAsync($"api/v1/agents/{a.AgentId}/configuration"); Assert.Equal(HttpStatusCode.Conflict,changed.StatusCode);
        Assert.False(changed.Headers.Contains(Receipt.OfferHeader));
        using var oldReceipt=await Post(a,Decision(a,id)); oldReceipt.EnsureSuccessStatusCode();
        Assert.Null((await State(a)).Current); Assert.Equal(id,(await State(a)).LastAccepted!.OfferId);
    }

    [ServerFact]
    public async Task UpgradeFromSchemaTwoPreservesAgentConfigAndRunHistory()
    {
        var a=await fixture.EnrollAsync(); var run=new SPBackup.Server.Monitoring.MonitoredRun(Guid.NewGuid(),Guid.NewGuid(),"Old history",DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow,"Succeeded",0,false,"Full",[]);
        await fixture.Store.SaveRunAsync(a.AgentId,run,default); var original=await fixture.Store.ConfigurationAsync(a.AgentId,default);
        await fixture.ExecuteAsync("DROP TABLE server_configuration_history; DROP TABLE server_configuration_offers; DELETE FROM server_schema WHERE version>=3");
        Assert.False(await fixture.Store.ReadyAsync()); await fixture.Store.MigrateAsync(); await fixture.Store.MigrateAsync(); Assert.True(await fixture.Store.ReadyAsync());
        Assert.Equal(original,await fixture.Store.ConfigurationAsync(a.AgentId,default)); Assert.Contains((await fixture.Store.RunsAsync(a.AgentId,0,default)).Runs,r=>r.RunId==run.RunId);
        Assert.Equal(a.AgentId,await fixture.Store.AuthenticateAgentAsync(a.Token,default)); _=await Offer(a);
    }

    [ServerFact]
    public async Task FailedSchemaThreeMigrationRollsBackItsTableAndPreservesSchemaTwo()
    {
        var a=await fixture.EnrollAsync(); var original=await fixture.Store.ConfigurationAsync(a.AgentId,default);
        await fixture.ExecuteAsync("DROP TABLE server_configuration_history; DROP TABLE server_configuration_offers; DELETE FROM server_schema WHERE version>=3; CREATE FUNCTION fail_schema_three() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.version=3 THEN RAISE EXCEPTION 'fixture'; END IF; RETURN NEW; END $$; CREATE TRIGGER fail_schema_three BEFORE INSERT ON server_schema FOR EACH ROW EXECUTE FUNCTION fail_schema_three()");
        try
        {
            await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>fixture.Store.MigrateAsync());
            await using var db=new Npgsql.NpgsqlConnection(File.ReadAllText(Path.Combine(fixture.Root,"connection.txt"))); await db.OpenAsync();
            await using var check=new Npgsql.NpgsqlCommand("SELECT max(version)=2 AND to_regclass('server_configuration_offers') IS NULL FROM server_schema",db);
            Assert.Equal(true,await check.ExecuteScalarAsync()); Assert.Equal(original,await fixture.Store.ConfigurationAsync(a.AgentId,default));
        }
        finally
        {
            await fixture.ExecuteAsync("DROP TRIGGER fail_schema_three ON server_schema; DROP FUNCTION fail_schema_three()");
            await fixture.Store.MigrateAsync();
        }
        Assert.True(await fixture.Store.ReadyAsync());
    }
}
