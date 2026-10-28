using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using SPBackup.Core;
using Xunit;
using Caps = SPBackup.Server.AgentCapabilities;
using CoreCaps = SPBackup.Core.AgentCapabilities;
using Snapshot = SPBackup.Core.Monitoring.MonitoringSnapshot;

namespace SPBackup.Server.Tests;

public sealed class CapabilityTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private static readonly string[] Execution = [Caps.ConfigurationV1, Caps.StrictConfigurationV1, Caps.ManagedExecutionV1];
    private sealed class Credentials(string value) : IAgentCredentialProvider
    { public Task<string> GetBearerTokenAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(value); }

    private HttpClient Agent(string bearer)
    {
        var client = fixture.Client(); client.DefaultRequestHeaders.Authorization = new("Bearer", bearer); return client;
    }

    private async Task Configure(Guid agent, bool tls = true, string spelling = "normal", long revision = 1)
    {
        var pg = new JsonObject { ["host"] = "db.example.test", ["tls"] = tls ? new JsonObject { ["rootCertificatePath"] = @"C:\CA\root.pem" } : null };
        var job = new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["name"] = "PRIVATE_DATABASE_NAME", ["kind"] = "PostgreSql" };
        if (spelling == "duplicate") job["postgreSql"] = null;
        job[spelling == "normal" ? "postgreSql" : "PostgreSQL"] = pg;
        var doc = new JsonObject { ["agentId"] = agent.ToString(), ["revision"] = revision, ["protocolVersion"] = 1, ["jobs"] = new JsonArray(job) };
        await fixture.ExecuteAsync("UPDATE server_configurations SET revision=@revision,document=@doc::jsonb WHERE agent_id=@id",
            ("revision", revision), ("doc", doc.ToJsonString()), ("id", agent));
    }

    [ServerFact]
    public async Task LegacyEmptyConfigurationAndConditionalRequestRemainCompatible()
    {
        var enrolled = await fixture.EnrollAsync(); using var client = Agent(enrolled.Token);
        using var initial = await client.GetAsync($"api/v1/agents/{enrolled.AgentId}/configuration");
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode); Assert.Equal("\"1\"", initial.Headers.ETag!.Tag);
        Assert.Contains(Caps.Header, initial.Headers.Vary);
        Assert.True(initial.Headers.CacheControl!.Private && initial.Headers.CacheControl.NoCache);
        client.DefaultRequestHeaders.IfNoneMatch.Add(new("\"1\""));
        using var cached = await client.GetAsync($"api/v1/agents/{enrolled.AgentId}/configuration");
        Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);
    }

    [ServerTheory]
    [InlineData("normal")]
    [InlineData("mixed")]
    [InlineData("duplicate")]
    public async Task TlsRequirementCannotBeHiddenOrBypassedByNotModified(string spelling)
    {
        var enrolled = await fixture.EnrollAsync(); await Configure(enrolled.AgentId, spelling: spelling);
        using var client = Agent(enrolled.Token);
        var uri = $"api/v1/agents/{enrolled.AgentId}/configuration";
        using var legacy = await client.GetAsync(uri);
        Assert.Equal(HttpStatusCode.Conflict, legacy.StatusCode);
        Assert.DoesNotContain("PRIVATE_DATABASE_NAME", await legacy.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add(Caps.Header, string.Join(",", Execution));
        client.DefaultRequestHeaders.IfNoneMatch.Add(new("\"1\""));
        using var refused = await client.GetAsync(uri);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        client.DefaultRequestHeaders.Remove(Caps.Header);
        client.DefaultRequestHeaders.Add(Caps.Header, string.Join(",", Execution.Append(Caps.PostgreSqlTlsV1)));
        using var unchanged = await client.GetAsync(uri);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        client.DefaultRequestHeaders.IfNoneMatch.Clear();
        using var accepted = await client.GetAsync(uri); Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [ServerFact]
    public async Task PlainJobsDoNotRequireTlsButStillRequireManagedExecution()
    {
        var enrolled = await fixture.EnrollAsync(); await Configure(enrolled.AgentId, tls: false);
        using var client = Agent(enrolled.Token);
        client.DefaultRequestHeaders.Add(Caps.Header, string.Join(",", Execution));
        using var response = await client.GetAsync($"api/v1/agents/{enrolled.AgentId}/configuration");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [ServerFact]
    public async Task UnsupportedStoredProtocolCannotProduceNotModified()
    {
        var enrolled = await fixture.EnrollAsync();
        await fixture.ExecuteAsync("UPDATE server_configurations SET document=@doc::jsonb WHERE agent_id=@id",
            ("doc", JsonSerializer.Serialize(new { agentId = enrolled.AgentId, revision = 1, protocolVersion = 2, jobs = Array.Empty<object>() })),
            ("id", enrolled.AgentId));
        using var client = Agent(enrolled.Token); client.DefaultRequestHeaders.IfNoneMatch.Add(new("\"1\""));
        using var response = await client.GetAsync($"api/v1/agents/{enrolled.AgentId}/configuration");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("configuration_format_unsupported", await response.Content.ReadAsStringAsync());
    }

    [ServerFact]
    public async Task CurrentClientDoesNotCacheJobsForAnExecutorItDoesNotProvide()
    {
        var enrolled = await fixture.EnrollAsync();
        using var api = new AgentApiClient(new Credentials(enrolled.Token), fixture.Handler());
        var settings = new AgentSettings { Mode = AgentMode.Managed, AgentId = enrolled.AgentId, ServerAddress = fixture.Address.ToString(), CredentialKey = "test" };
        var directory = Path.Combine(fixture.Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "server-jobs.json");
        var cache = new AgentConfigurationCache(path);
        var config = new AgentConfiguration(new JobStore(Path.Combine(directory, "jobs.json")), cache, api);
        Assert.True((await config.SynchronizeAsync(settings, DateTimeOffset.UtcNow)).Succeeded);
        var previous = await File.ReadAllBytesAsync(path);
        await Configure(enrolled.AgentId, revision: 2);
        await api.SendHeartbeatAsync(settings, new(enrolled.AgentId, "test", true, 1, DateTimeOffset.UtcNow));
        var denied = await config.SynchronizeAsync(settings, DateTimeOffset.UtcNow);
        Assert.False(denied.Succeeded);
        Assert.Equal(HttpStatusCode.Conflict, Assert.IsType<HttpRequestException>(denied.Error).StatusCode);
        Assert.Equal(previous, await File.ReadAllBytesAsync(path));
        Assert.Equal(1, await config.GetActiveRevisionAsync(settings));
    }

    [ServerFact]
    public async Task RealStandaloneMonitoringReportsCapabilitiesWithoutTrustingBodyOrVersion()
    {
        var enrolled = await fixture.EnrollAsync();
        var settings = new ServerMonitoringSettings(fixture.Address.ToString(), enrolled.AgentId, "test");
        using var api = new ServerMonitoringClient(settings, fixture.Handler());
        var snapshot = new Snapshot(DateTimeOffset.UtcNow, "unrelated-version", [], null, 0)
            { ConfigurationCapabilities = [Caps.ManagedExecutionV1] };
        await api.SendAsync(settings, enrolled.Token, snapshot, []);
        var state = (await fixture.Store.MonitoringAsync(enrolled.AgentId, default))!;
        Assert.Equal(CoreCaps.Reported.Order(), state.Snapshot.ConfigurationCapabilities!.Order());
        Assert.DoesNotContain(Caps.ManagedExecutionV1, state.Snapshot.ConfigurationCapabilities!);
        using (var page = await fixture.Administrator.GetAsync($"agents/{enrolled.AgentId}"))
        {
            Assert.Contains("Задания настраиваются на компьютере агента", WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync()));
        }
        await fixture.RestartAsync();
        using var metadata = await fixture.Administrator.GetAsync($"api/v1/management/agents/{enrolled.AgentId}/capabilities");
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        var report = JsonNode.Parse(await metadata.Content.ReadAsStringAsync())!;
        Assert.NotNull(report["receivedUtc"]); Assert.Equal(3, report["capabilities"]!.AsArray().Count);
        using var old = Agent(enrolled.Token);
        using var posted = await old.PutAsync($"api/v1/agents/{enrolled.AgentId}/monitoring", ServerFixture.Json(snapshot));
        Assert.Equal(HttpStatusCode.NoContent, posted.StatusCode);
        Assert.Empty((await fixture.Store.MonitoringAsync(enrolled.AgentId, default))!.Snapshot.ConfigurationCapabilities!);
    }

    [ServerTheory]
    [InlineData("Invalid")]
    [InlineData("configuration-v1,,other")]
    [InlineData("secret:value")]
    public async Task MalformedHeaderDoesNotReplaceMonitoringSnapshot(string header)
    {
        var enrolled = await fixture.EnrollAsync(); using var client = Agent(enrolled.Token);
        var uri = $"api/v1/agents/{enrolled.AgentId}/monitoring";
        var snapshot = new Snapshot(DateTimeOffset.UtcNow, "fixture", [], null, 0);
        using var good = await client.PutAsync(uri, ServerFixture.Json(snapshot)); Assert.Equal(HttpStatusCode.NoContent, good.StatusCode);
        var before = (await fixture.Store.MonitoringAsync(enrolled.AgentId, default))!;
        client.DefaultRequestHeaders.Add(Caps.Header, header);
        using var bad = await client.PutAsync(uri, ServerFixture.Json(snapshot with { AgentVersion = "must-not-save" }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var after = (await fixture.Store.MonitoringAsync(enrolled.AgentId, default))!;
        Assert.Equal(before.ReceivedUtc, after.ReceivedUtc); Assert.Equal("fixture", after.Snapshot.AgentVersion);
    }

    [ServerFact]
    public async Task CapabilityMetadataRequiresAdministratorAndUnknownAgentIsNotFound()
    {
        var enrolled = await fixture.EnrollAsync(); using var client = Agent(enrolled.Token);
        using var denied = await client.GetAsync($"api/v1/management/agents/{enrolled.AgentId}/capabilities");
        Assert.True(denied.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        using var missing = await fixture.Administrator.GetAsync($"api/v1/management/agents/{Guid.NewGuid()}/capabilities");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
