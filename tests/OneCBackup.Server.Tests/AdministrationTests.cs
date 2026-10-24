using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OneCBackup.Server;
using Xunit;

namespace OneCBackup.Server.Tests;

public sealed class AdministrationTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    [ServerFact]
    public async Task DiagnosticsOnlyExportsWhitelistedMetadataAndLeavesConfigurationUntouched()
    {
        var enrolled = await fixture.EnrollAsync();
        var before = await fixture.Store.ConfigurationAsync(enrolled.AgentId, default);
        var log = fixture.App.Services.GetRequiredService<ServerLog>();
        var secret = "fixture-private-password-" + Guid.NewGuid();
        log.Write(ServerEvent.RequestFailed, new IOException(secret));
        var directory = Path.Combine(fixture.Root, "data", "logs");
        var owned = Path.Combine(directory, "server-20260101-" + Guid.NewGuid().ToString("N") + "-000001.jsonl");
        await File.WriteAllTextAsync(owned, JsonSerializer.Serialize(new { Utc = DateTimeOffset.UtcNow,
            Event = "LoginFailed", HResult = (int?)null, Password = secret, Token = enrolled.Token, Frames = new[] { secret } }) + "\ninvalid " + secret);
        await File.WriteAllTextAsync(Path.Combine(directory, "private-settings.json"), secret);
        using (var oversized = File.Create(Path.Combine(directory, "server-20260101-" + Guid.NewGuid().ToString("N") + "-000002.jsonl")))
            oversized.SetLength(2 * 1024 * 1024 + 1);
        using var response = await fixture.Administrator.PostAsync("api/v1/management/diagnostics", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var bytes = await response.Content.ReadAsByteArrayAsync(); Assert.True(bytes.Length < 512 * 1024);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.Equal(new[] { "README.txt", "events.json", "server.json" }, zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open()); var text = await reader.ReadToEndAsync();
            foreach (var value in fixture.Secrets.Append(secret).Append(fixture.Root)) Assert.DoesNotContain(value, text);
            Assert.DoesNotContain("Host=", text); Assert.DoesNotContain("PRIVATE KEY", text);
        }
        using var summary = JsonDocument.Parse(zip.GetEntry("server.json")!.Open());
        Assert.True(summary.RootElement.GetProperty("DatabaseReady").GetBoolean());
        using var events = JsonDocument.Parse(zip.GetEntry("events.json")!.Open());
        Assert.Contains(events.RootElement.GetProperty("Events").EnumerateArray(), e => e.GetProperty("Event").GetString() == "LoginFailed");
        Assert.True(events.RootElement.GetProperty("SkippedRecords").GetInt32() > 0);
        Assert.Equal(before, await fixture.Store.ConfigurationAsync(enrolled.AgentId, default));
    }

    [ServerFact]
    public async Task MaintenanceRequiresAdministratorAndDiagnosticsRequiresCsrf()
    {
        using var anonymous = fixture.Client();
        using var absent = await anonymous.PostAsync("api/v1/management/diagnostics", null); Assert.Equal(HttpStatusCode.Unauthorized, absent.StatusCode);
        using var audit = await anonymous.GetAsync("api/v1/management/audit"); Assert.Equal(HttpStatusCode.Unauthorized, audit.StatusCode);
        var enrolled = await fixture.EnrollAsync(); using var agent = fixture.Client();
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", enrolled.Token);
        using var forbidden = await agent.PostAsync("api/v1/management/diagnostics", null); Assert.Equal(HttpStatusCode.Unauthorized, forbidden.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/management/diagnostics"); request.Headers.Add("X-CSRF-TOKEN", "wrong");
        using var noCsrf = await fixture.Administrator.SendAsync(request); Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        using var system = await fixture.Administrator.GetAsync("system");
        var html = WebUtility.HtmlDecode(await system.Content.ReadAsStringAsync());
        Assert.Contains("Скачать диагностику сервера", html); Assert.Contains("Сменить пароль администратора", html); Assert.Contains("Журнал действий", html);
    }

    [ServerFact]
    public async Task AuditPaginationShowsActualActionsAndPersistsAfterRestart()
    {
        var enrolled = await fixture.EnrollAsync();
        using var revoked = await fixture.Administrator.PostAsync("api/v1/management/agents/" + enrolled.AgentId + "/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        var page = await fixture.Store.AuditPageAsync(0);
        Assert.Contains(page.Entries, e => e.Event == "AgentRevoked" && e.AgentId == enrolled.AgentId && e.UserName == "administrator");
        Assert.Contains(page.Entries, e => e.Event == "AdministratorLogin");
        await fixture.ExecuteAsync("INSERT INTO server_audit(event) SELECT 'InvitationCreated' FROM generate_series(1,55)");
        var first = await fixture.Store.AuditPageAsync(0); var second = await fixture.Store.AuditPageAsync(1);
        Assert.Equal(50, first.Entries.Count); Assert.NotEmpty(second.Entries);
        Assert.Empty(first.Entries.Select(e => e.Id).Intersect(second.Entries.Select(e => e.Id)));
        using var invalid = await fixture.Administrator.GetAsync("api/v1/management/audit?page=-1"); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        await fixture.RestartAsync();
        Assert.Equal(first.Total, (await fixture.Store.AuditPageAsync(0)).Total);
        using var panel = await fixture.Administrator.GetAsync("audit?page=1"); panel.EnsureSuccessStatusCode();
        Assert.Contains("Отозван доступ агента", WebUtility.HtmlDecode(await panel.Content.ReadAsStringAsync()));
    }

    [ServerFact]
    public async Task PasswordAndAuditRollBackTogetherWhenAuditCannotBeWritten()
    {
        var user = (await fixture.Store.LoginAsync("administrator", fixture.Password, default))!;
        await fixture.ExecuteAsync("""
            CREATE FUNCTION refuse_password_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.event='AdministratorPasswordChanged' THEN RAISE EXCEPTION 'fixture failure'; END IF; RETURN NEW; END; $$;
            CREATE TRIGGER refuse_password_audit BEFORE INSERT ON server_audit FOR EACH ROW EXECUTE FUNCTION refuse_password_audit();
            """);
        try
        {
            var next = ServerTokens.Create();
            await Assert.ThrowsAsync<PostgresException>(() => fixture.Store.ChangePasswordAsync(user.Id, user.SecurityStamp, fixture.Password, next, next));
            Assert.NotNull(await fixture.Store.LoginAsync("administrator", fixture.Password, default));
            Assert.True(await fixture.Store.ValidateSessionAsync(user.Id, user.SecurityStamp, default));
        }
        finally { await fixture.ExecuteAsync("DROP TRIGGER refuse_password_audit ON server_audit; DROP FUNCTION refuse_password_audit()"); }
    }
}

public sealed class AdministratorPasswordTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    [ServerFact]
    public async Task PasswordChangeValidatesFormRevokesSessionsSurvivesRestartAndKeepsAgentsConnected()
    {
        var enrolled = await fixture.EnrollAsync();
        using var other = fixture.Client(); await fixture.SignInAsync(other);
        using var account = await fixture.Administrator.GetAsync("account"); account.EnsureSuccessStatusCode();
        Assert.DoesNotContain(fixture.Password, await account.Content.ReadAsStringAsync());
        var next = ServerTokens.Create(); fixture.Secrets.Add(next);
        async Task<HttpResponseMessage> Change(string current, string value, string confirmation, bool invalidCsrf = false)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "management/password")
            { Content = new FormUrlEncodedContent(new Dictionary<string,string> { ["currentPassword"] = current, ["newPassword"] = value, ["confirmation"] = confirmation }) };
            if (invalidCsrf) request.Headers.Add("X-CSRF-TOKEN", "invalid");
            using (request) return await fixture.Administrator.SendAsync(request);
        }
        using (var csrf = await Change(fixture.Password, next, next, true)) Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode);
        using (var wrong = await Change("wrong", next, next)) Assert.Contains("error=current", wrong.Headers.Location!.OriginalString);
        using (var weak = await Change(fixture.Password, "short", "short")) Assert.Contains("error=weak", weak.Headers.Location!.OriginalString);
        using (var mismatch = await Change(fixture.Password, next, next + "x")) Assert.Contains("error=mismatch", mismatch.Headers.Location!.OriginalString);
        using (var same = await Change(fixture.Password, fixture.Password, fixture.Password)) Assert.Contains("error=same", same.Headers.Location!.OriginalString);
        Assert.NotNull(await fixture.Store.LoginAsync("administrator", fixture.Password, default));
        using (var success = await Change(fixture.Password, next, next))
        { Assert.Equal(HttpStatusCode.Redirect, success.StatusCode); Assert.EndsWith("/login?changed=1", success.Headers.Location!.OriginalString); }
        using (var oldSession = await other.GetAsync("api/v1/management/session")) Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);
        using (var thisSession = await fixture.Administrator.GetAsync("api/v1/management/session")) Assert.Equal(HttpStatusCode.Unauthorized, thisSession.StatusCode);
        Assert.Null(await fixture.Store.LoginAsync("administrator", fixture.Password, default));
        using var fresh = fixture.Client(); await fixture.SignInAsync(fresh, password: next);
        using (var panel = await fresh.GetAsync("system")) Assert.Equal(HttpStatusCode.OK, panel.StatusCode);
        using var agent = fixture.Client(); agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", enrolled.Token);
        using (var configuration = await agent.GetAsync("api/v1/agents/" + enrolled.AgentId + "/configuration")) Assert.Equal(HttpStatusCode.OK, configuration.StatusCode);
        Assert.Contains((await fixture.Store.AuditPageAsync(0)).Entries, e => e.Event == "AdministratorPasswordChanged");
        await fixture.RestartAsync();
        using var afterRestart = fixture.Client(); await fixture.SignInAsync(afterRestart, password: next);
        using var ready = await afterRestart.GetAsync("system"); Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }
}

public sealed class PasswordChangeConcurrencyTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    [ServerFact]
    public async Task ConcurrentRequestsWithOldSessionOnlyChangePasswordOnce()
    {
        var user = (await fixture.Store.LoginAsync("administrator", fixture.Password, default))!;
        var first = ServerTokens.Create(); var second = ServerTokens.Create();
        var result = await Task.WhenAll(fixture.Store.ChangePasswordAsync(user.Id, user.SecurityStamp, fixture.Password, first, first),
            fixture.Store.ChangePasswordAsync(user.Id, user.SecurityStamp, fixture.Password, second, second));
        Assert.Single(result, item => item == PasswordChangeResult.Changed); Assert.Single(result, item => item == PasswordChangeResult.StaleSession);
        Assert.Equal(1, (await fixture.Store.AuditPageAsync(0)).Entries.Count(e => e.Event == "AdministratorPasswordChanged"));
        Assert.False(await fixture.Store.ValidateSessionAsync(user.Id, user.SecurityStamp, default));
    }
}
