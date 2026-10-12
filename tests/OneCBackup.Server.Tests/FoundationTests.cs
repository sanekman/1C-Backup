using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OneCBackup.Core;
using OneCBackup.Server;
using Xunit;

namespace OneCBackup.Server.Tests;

public sealed class FoundationTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private sealed class Credentials(string token) : IAgentCredentialProvider
    { public Task<string> GetBearerTokenAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(token); }
    [ServerFact]
    public async Task EmptyOrUninitializedDatabaseIsNotReadyUntilFirstAdministratorExists()
    {
        var schema = "server_test_empty_" + Guid.NewGuid().ToString("N");
        await fixture.ExecuteAsync("CREATE SCHEMA " + schema);
        try
        {
            var connection = new Npgsql.NpgsqlConnectionStringBuilder(File.ReadAllText(Path.Combine(fixture.Root, "connection.txt"))) { SearchPath = schema };
            await using var source = Npgsql.NpgsqlDataSource.Create(connection.ConnectionString);
            var store = new ServerStore(source);
            Assert.False(await store.ReadyAsync()); await store.MigrateAsync(); Assert.False(await store.ReadyAsync());
            var invalid = await Assert.ThrowsAsync<ServerRequestException>(() => store.BootstrapAsync("admin", "short"));
            Assert.Equal("invalid_bootstrap_credentials", invalid.Code); Assert.False(await store.ReadyAsync());
            await store.BootstrapAsync("admin", ServerTokens.Create()); Assert.True(await store.ReadyAsync());
        }
        finally { await fixture.ExecuteAsync("DROP SCHEMA " + schema + " CASCADE"); }
    }
    [ServerFact]
    public async Task ExistingAgentClientUsesActualTrustedHttpsConfigurationAndHeartbeat()
    {
        var enrolled = await fixture.EnrollAsync();
        using var api = new AgentApiClient(new Credentials(enrolled.Token), fixture.Handler());
        var settings = new AgentSettings { Mode = AgentMode.Managed, AgentId = enrolled.AgentId, ServerAddress = fixture.Address.ToString(), CredentialKey = "fixture" };
        var configuration = await api.GetConfigurationAsync(settings, null);
        Assert.Equal(enrolled.AgentId, configuration!.AgentId); Assert.Equal(1, configuration.Revision); Assert.Empty(configuration.Jobs);
        Assert.Null(await api.GetConfigurationAsync(settings, 1));
        await api.SendHeartbeatAsync(settings, new(enrolled.AgentId, "0.1.0-test", false, 1, DateTimeOffset.Parse("2000-01-01T00:00:00Z")));
        var stored = await fixture.Store.AgentAsync(enrolled.AgentId, default);
        Assert.Equal(1, stored!.ReportedRevision); Assert.False(stored.BackupExecutionAvailable);
        Assert.True(stored.ReceivedUtc > DateTimeOffset.UtcNow.AddMinutes(-1)); Assert.Equal(2000, stored.SentUtc!.Value.Year);
        using var page = await fixture.Administrator.GetAsync("agents/" + enrolled.AgentId);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode); Assert.Contains("Ревизия", await page.Content.ReadAsStringAsync());
    }
    [ServerFact]
    public async Task EnrollmentIsSingleUseAndDoesNotReplaceAnExistingAgent()
    {
        var id = Guid.NewGuid(); var invitation = await fixture.InviteAsync(id);
        using var client = fixture.Client(); var request = new EnrollmentRequest(invitation.Code, id, "First");
        using var first = await client.PostAsync("api/v1/enrollment", ServerFixture.Json(request)); Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var replay = await client.PostAsync("api/v1/enrollment", ServerFixture.Json(request)); Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        var secondInvite = await fixture.InviteAsync(id);
        using var duplicate = await client.PostAsync("api/v1/enrollment", ServerFixture.Json(request with { InvitationCode = secondInvite.Code }));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("First", (await fixture.Store.AgentAsync(id, default))!.Name);
    }
    [ServerFact]
    public async Task ConcurrentInvitationConsumptionHasOnlyOneWinner()
    {
        var invitation = await fixture.InviteAsync();
        using var first = fixture.Client(); using var second = fixture.Client();
        var results = await Task.WhenAll(first.PostAsync("api/v1/enrollment", ServerFixture.Json(new EnrollmentRequest(invitation.Code, Guid.NewGuid(), "A"))),
            second.PostAsync("api/v1/enrollment", ServerFixture.Json(new EnrollmentRequest(invitation.Code, Guid.NewGuid(), "B"))));
        Assert.Single(results, item => item.StatusCode == HttpStatusCode.Created); Assert.Single(results, item => item.StatusCode == HttpStatusCode.Forbidden);
        foreach (var result in results) result.Dispose();
    }
    [ServerFact]
    public async Task InvitationExpiryAndAgentBindingAreEnforcedWithoutConsumingMismatch()
    {
        var id = Guid.NewGuid(); var invitation = await fixture.InviteAsync(id); using var client = fixture.Client();
        using var mismatch = await client.PostAsync("api/v1/enrollment", ServerFixture.Json(new EnrollmentRequest(invitation.Code, Guid.NewGuid(), "Wrong")));
        Assert.Equal(HttpStatusCode.Forbidden, mismatch.StatusCode);
        using var correct = await client.PostAsync("api/v1/enrollment", ServerFixture.Json(new EnrollmentRequest(invitation.Code, id, "Correct")));
        Assert.Equal(HttpStatusCode.Created, correct.StatusCode);
        var expired = await fixture.InviteAsync();
        await fixture.ExecuteAsync("UPDATE server_invitations SET expires_utc=now()-interval '1 minute' WHERE id=@id", ("id", expired.InvitationId));
        using var refused = await client.PostAsync("api/v1/enrollment", ServerFixture.Json(new EnrollmentRequest(expired.Code, Guid.NewGuid(), "Expired")));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }
    [ServerFact]
    public async Task AgentCannotReadAnotherAgentOrUseAdministratorRoutes()
    {
        var enrolled = await fixture.EnrollAsync(); using var client = fixture.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", enrolled.Token);
        using var other = await client.GetAsync("api/v1/agents/" + Guid.NewGuid() + "/configuration"); Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        using var management = await client.GetAsync("api/v1/management/agents"); Assert.Equal(HttpStatusCode.Unauthorized, management.StatusCode);
        using var anonymous = fixture.Client();
        using var hidden = await anonymous.GetAsync("api/v1/management/agents"); Assert.Equal(HttpStatusCode.Unauthorized, hidden.StatusCode);
        using var noToken = await anonymous.GetAsync("api/v1/agents/" + enrolled.AgentId + "/configuration"); Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
    }
    [ServerFact]
    public async Task HeartbeatRejectsMismatchedBodyAndFutureRevisionWithoutChangingStoredState()
    {
        var enrolled = await fixture.EnrollAsync(); using var client = fixture.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", enrolled.Token);
        var endpoint = "api/v1/agents/" + enrolled.AgentId + "/heartbeat";
        using var mismatch = await client.PostAsync(endpoint, ServerFixture.Json(new HeartbeatRequest(Guid.NewGuid(), "test", true, 1, DateTimeOffset.UtcNow)));
        Assert.Equal(HttpStatusCode.Forbidden, mismatch.StatusCode);
        using var future = await client.PostAsync(endpoint, ServerFixture.Json(new HeartbeatRequest(enrolled.AgentId, "test", true, 2, DateTimeOffset.UtcNow)));
        Assert.Equal(HttpStatusCode.Conflict, future.StatusCode);
        Assert.Null((await fixture.Store.AgentAsync(enrolled.AgentId, default))!.ReceivedUtc);
    }
    [ServerFact]
    public async Task CookieManagementRequiresAntiforgeryAndRevocationIsImmediate()
    {
        var enrolled = await fixture.EnrollAsync();
        using var missing = new HttpRequestMessage(HttpMethod.Post, "api/v1/management/agents/" + enrolled.AgentId + "/revoke");
        missing.Headers.Add("X-CSRF-TOKEN", "invalid");
        using var rejected = await fixture.Administrator.SendAsync(missing); Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var before = fixture.Client(); before.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", enrolled.Token);
        using var configuration = await before.GetAsync("api/v1/agents/" + enrolled.AgentId + "/configuration"); Assert.Equal(HttpStatusCode.OK, configuration.StatusCode);
        using var revoke = await fixture.Administrator.PostAsync("api/v1/management/agents/" + enrolled.AgentId + "/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        using var refused = await before.GetAsync("api/v1/agents/" + enrolled.AgentId + "/configuration"); Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.True((await fixture.Store.AgentAsync(enrolled.AgentId, default))!.Revoked);
    }
    [ServerFact]
    public async Task MigrationsAreRepeatableAndBootstrapNeverResetsAdministrator()
    {
        await Task.WhenAll(fixture.Store.MigrateAsync(), fixture.Store.MigrateAsync());
        var conflict = await Assert.ThrowsAsync<ServerRequestException>(() => fixture.Store.BootstrapAsync("another", ServerTokens.Create()));
        Assert.Equal("administrator_already_exists", conflict.Code);
        Assert.NotNull(await fixture.Store.LoginAsync("administrator", fixture.Password, default));
        Assert.Null(await fixture.Store.LoginAsync("administrator' OR 1=1--", fixture.Password, default));
        Assert.Null(await fixture.Store.LoginAsync("administrator", ServerTokens.Create(), default));
    }
    [ServerFact]
    public async Task BrowserFormCreatesInvitationAndEscapesNames()
    {
        using var connect = await fixture.Administrator.GetAsync("connect"); connect.EnsureSuccessStatusCode();
        var html = await connect.Content.ReadAsStringAsync(); var anti = ServerFixture.AntiToken(html);
        Assert.False(string.IsNullOrEmpty(anti));
        using var response = await fixture.Administrator.PostAsync("connect", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["__RequestVerificationToken"] = anti, ["_handler"] = "invitation", ["Model.Name"] = "Browser invitation", ["Model.Minutes"] = "15" }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadAsStringAsync(); Assert.Contains("Приглашение готово", result);
        var invitation = await fixture.InviteAsync(); using var client = fixture.Client();
        using var enrollment = await client.PostAsync("api/v1/enrollment", ServerFixture.Json(new EnrollmentRequest(invitation.Code, Guid.NewGuid(), "<script>alert(1)</script>")));
        Assert.Equal(HttpStatusCode.Created, enrollment.StatusCode);
        using var home = await fixture.Administrator.GetAsync(""); var content = await home.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<script>alert(1)</script>", content); Assert.Contains("&lt;script&gt;", content);
    }
    [ServerFact]
    public async Task RestartPreservesAuthenticationAndAgentConfiguration()
    {
        var enrolled = await fixture.EnrollAsync(); await fixture.RestartAsync();
        using var session = await fixture.Administrator.GetAsync("api/v1/management/session"); Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        using var client = fixture.Client(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", enrolled.Token);
        using var configuration = await client.GetAsync("api/v1/agents/" + enrolled.AgentId + "/configuration"); Assert.Equal(HttpStatusCode.OK, configuration.StatusCode);
        Assert.Equal(1, (await fixture.Store.ConfigurationAsync(enrolled.AgentId, default)).Revision);
    }
    [ServerFact]
    public async Task PrefixHostingWorksWithExistingAgentClient()
    {
        var enrolled = await fixture.EnrollAsync(); await using var app = fixture.Build("/backup"); await app.StartAsync();
        using var api = new AgentApiClient(new Credentials(enrolled.Token), fixture.Handler());
        var address = app.Urls.Single() + "/backup/";
        var settings = new AgentSettings { Mode = AgentMode.Managed, AgentId = enrolled.AgentId, ServerAddress = address, CredentialKey = "fixture" };
        Assert.Equal(1, (await api.GetConfigurationAsync(settings, null))!.Revision);
        using var client = new HttpClient(fixture.Handler()) { BaseAddress = new Uri(address) };
        await fixture.SignInAsync(client);
        using var page = await client.GetAsync("connect"); page.EnsureSuccessStatusCode();
        Assert.Contains("/backup/", await page.Content.ReadAsStringAsync());
        await app.StopAsync();
    }
    [ServerFact]
    public async Task ProductionRefusesPlainHttpEvenWithSpoofedForwardedHeaders()
    {
        await using var app = fixture.Build(plainHttp: true, automaticForwarding: true); await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        using var refused = await client.GetAsync("api/v1/management/agents"); Assert.Equal((HttpStatusCode)426, refused.StatusCode);
        using var health = await client.GetAsync("health/ready"); Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        await app.StopAsync();
        await using var trusted = fixture.Build(plainHttp: true, trustedProxy: true); await trusted.StartAsync();
        using var forwarded = new HttpClient { BaseAddress = new Uri(trusted.Urls.Single()) };
        forwarded.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        using var authenticatedRoute = await forwarded.GetAsync("api/v1/management/agents"); Assert.Equal(HttpStatusCode.Unauthorized, authenticatedRoute.StatusCode);
        await trusted.StopAsync();
    }
    [ServerFact]
    public async Task LogsAndManagementResponsesDoNotExposeCredentials()
    {
        var enrolled = await fixture.EnrollAsync();
        fixture.App.Services.GetRequiredService<ServerLog>().Write(ServerEvent.RequestFailed, new Exception("password=" + fixture.Password));
        using var response = await fixture.Administrator.GetAsync("api/v1/management/agents"); var body = await response.Content.ReadAsStringAsync();
        foreach (var secret in fixture.Secrets) Assert.DoesNotContain(secret, body);
        foreach (var path in Directory.GetFiles(Path.Combine(fixture.Root, "data", "logs"), "*.jsonl"))
        {
            var text = File.ReadAllText(path);
            foreach (var secret in fixture.Secrets) Assert.DoesNotContain(secret, text);
            Assert.DoesNotContain("password=", text); Assert.DoesNotContain(fixture.Root, text);
        }
        Assert.DoesNotContain("tokenHash", body, StringComparison.OrdinalIgnoreCase);
    }
}
