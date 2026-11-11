using System.Net;
using System.Text.Json;
using SPBackup.Server;
using SPBackup.Server.Monitoring;
using Xunit;

namespace SPBackup.Server.Tests;
public sealed class CentralManagementTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Commands(Guid id) => $"api/v1/management/agents/{id}/commands";
    private async Task<(EnrollmentResponse Agent, ManagedFileJob Job, HttpClient Client)> Ready()
    {
        var agent = await fixture.EnrollAsync();
        var job = new ManagedFileJob { Id = Guid.NewGuid(), Name = "Central test", Source = @"C:\Data", DestinationDirectory = @"D:\Copies" };
        using var update = new HttpRequestMessage(HttpMethod.Put, $"api/v1/management/agents/{agent.AgentId}/configuration");
        update.Headers.IfMatch.Add(new("\"1\"")); update.Content = ServerFixture.Json(new PublishFileConfiguration(agent.AgentId, 1, [job]));
        using var response = await fixture.Administrator.SendAsync(update); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var client = fixture.Client(); client.DefaultRequestHeaders.Authorization = new("Bearer", agent.Token);
        client.DefaultRequestHeaders.Add(AgentCapabilities.Header, "configuration-v1,strict-configuration-v1,managed-execution-v1,remote-runs-v1,managed-secrets-v1");
        await fixture.Store.SaveMonitoringAsync(agent.AgentId, new(DateTimeOffset.UtcNow, "stage5", [new(job.Id, job.Name, "FilesAndFolders", false, "Manual", null, 1)], null, 0)
            { ExecutionMode = "Managed", ConfigurationRevision = 2, BackupExecutionAvailable = true, ConfigurationCapabilities = [AgentCapabilities.RemoteRunsV1] }, default);
        return (agent, job, client);
    }
    private async Task<RemoteRunState> Submit(Guid agent, RemoteRunRequest request)
    {
        using var response = await fixture.Administrator.PostAsync(Commands(agent), ServerFixture.Json(request));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return JsonSerializer.Deserialize<RemoteRunState>(await response.Content.ReadAsStringAsync(), Json)!;
    }
    private static async Task Receipt(HttpClient client, Guid agent, RemoteRunReceipt receipt, HttpStatusCode expected = HttpStatusCode.NoContent)
    {
        using var result = await client.PostAsync($"api/v1/agents/{agent}/commands/receipt", ServerFixture.Json(receipt));
        Assert.Equal(expected, result.StatusCode);
    }
    [ServerFact]
    public async Task CommandsAreDurableIdempotentAndCannotClaimAResultBeforeExecution()
    {
        var (agent, job, client) = await Ready(); using var scope = client;
        var request = new RemoteRunRequest(Guid.NewGuid(), job.Id, 2);
        var created = await Submit(agent.AgentId, request);
        Assert.Equal("Queued", created.Status); Assert.Equal(created, await Submit(agent.AgentId, request));
        await Receipt(client, agent.AgentId, new(request.CommandId, "Succeeded", Guid.NewGuid()), HttpStatusCode.Conflict);
        using (var poll = await client.GetAsync($"api/v1/agents/{agent.AgentId}/commands"))
            Assert.Contains(request.CommandId.ToString(), await poll.Content.ReadAsStringAsync());
        await Receipt(client, agent.AgentId, new(request.CommandId, "Accepted"));
        await Receipt(client, agent.AgentId, new(request.CommandId, "Running"));
        await Receipt(client, agent.AgentId, new(request.CommandId, "Accepted"));
        Assert.Equal("Running", Assert.Single(await fixture.Store.RemoteRunsAsync(agent.AgentId, default)).Status);
        var result = new RemoteRunReceipt(request.CommandId, "Succeeded", Guid.NewGuid());
        await Receipt(client, agent.AgentId, result); var finished = Assert.Single(await fixture.Store.RemoteRunsAsync(agent.AgentId, default));
        await Receipt(client, agent.AgentId, result);
        Assert.Equal(finished, Assert.Single(await fixture.Store.RemoteRunsAsync(agent.AgentId, default)));
        await fixture.RestartAsync();
        Assert.Equal(finished, Assert.Single(await fixture.Store.RemoteRunsAsync(agent.AgentId, default)));
        Assert.Equal(1, (await fixture.Store.AuditPageAsync(0)).Entries.Count(a => a.AgentId == agent.AgentId && a.Event == "RemoteRunQueued"));
        Assert.Equal(1, (await fixture.Store.AuditPageAsync(0)).Entries.Count(a => a.AgentId == agent.AgentId && a.Event == "RemoteRunSucceeded"));
    }
    [ServerFact]
    public async Task ExpiredChangedRevisionAndNewEnrollmentCannotStartOldCommands()
    {
        var (agent, job, client) = await Ready(); using var scope = client;
        var request = new RemoteRunRequest(Guid.NewGuid(), job.Id, 2);
        await Submit(agent.AgentId, request);
        await fixture.ExecuteAsync("UPDATE server_remote_runs SET expires_utc=now()-interval '1 minute' WHERE command_id=@id", ("id", request.CommandId));
        using (var poll = await client.GetAsync($"api/v1/agents/{agent.AgentId}/commands")) Assert.Equal("[]", await poll.Content.ReadAsStringAsync());
        Assert.Equal("Expired", Assert.Single(await fixture.Store.RemoteRunsAsync(agent.AgentId, default)).Status);
        await Receipt(client, agent.AgentId, new(request.CommandId, "Accepted"), HttpStatusCode.Conflict);
        var second = request with { CommandId = Guid.NewGuid() }; await Submit(agent.AgentId, second);
        await fixture.ExecuteAsync("UPDATE server_configurations SET revision=3,document=jsonb_set(document,'{revision}','3') WHERE agent_id=@id", ("id", agent.AgentId));
        await Receipt(client, agent.AgentId, new(second.CommandId, "Accepted"), HttpStatusCode.Conflict);
        var enrolled = await fixture.EnrollAsync(agent.AgentId);
        using var next = fixture.Client(); next.DefaultRequestHeaders.Authorization = new("Bearer", enrolled.Token);
        next.DefaultRequestHeaders.Add(AgentCapabilities.Header, AgentCapabilities.RemoteRunsV1);
        using var nextPoll = await next.GetAsync($"api/v1/agents/{agent.AgentId}/commands"); Assert.Equal("[]", await nextPoll.Content.ReadAsStringAsync());
        Assert.Contains(await fixture.Store.RemoteRunsAsync(agent.AgentId, default), r => r.Command.CommandId == second.CommandId && r.Status == "Rejected");
    }
    [ServerFact]
    public async Task AuthorizationCsrfStrictBodiesAndAgentIsolationApplyToCommands()
    {
        var (agent, job, client) = await Ready(); using var scope = client;
        var other = await fixture.EnrollAsync();
        using (var foreign = await client.GetAsync($"api/v1/agents/{other.AgentId}/commands")) Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        using (var unauthenticated = fixture.Client())
        using (var denied = await unauthenticated.PostAsync(Commands(agent.AgentId), ServerFixture.Json(new RemoteRunRequest(Guid.NewGuid(), job.Id, 2)))) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using (var signed = fixture.Client())
        {
            await fixture.SignInAsync(signed);
            using var noCsrf = await signed.PostAsync(Commands(agent.AgentId), ServerFixture.Json(new RemoteRunRequest(Guid.NewGuid(), job.Id, 2)));
            Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        }
        using var secret = await fixture.Administrator.PostAsync(Commands(agent.AgentId), new StringContent("{\"commandId\":\"" + Guid.NewGuid() + "\",\"jobId\":\"" + job.Id + "\",\"revision\":2,\"password\":\"not-accepted\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, secret.StatusCode);
        Assert.Empty(await fixture.Store.RemoteRunsAsync(agent.AgentId, default));
    }
    [ServerFact]
    public async Task RemoteCommandAndAuditRollbackTogether()
    {
        var (agent, job, client) = await Ready(); using var scope = client;
        await fixture.ExecuteAsync("CREATE FUNCTION reject_remote_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.event LIKE 'RemoteRun%' THEN RAISE EXCEPTION 'test'; END IF; RETURN NEW; END $$; CREATE TRIGGER reject_remote_audit BEFORE INSERT ON server_audit FOR EACH ROW EXECUTE FUNCTION reject_remote_audit()");
        try
        {
            using var rejected = await fixture.Administrator.PostAsync(Commands(agent.AgentId), ServerFixture.Json(new RemoteRunRequest(Guid.NewGuid(), job.Id, 2)));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode); Assert.Empty(await fixture.Store.RemoteRunsAsync(agent.AgentId, default));
        }
        finally { await fixture.ExecuteAsync("DROP TRIGGER reject_remote_audit ON server_audit; DROP FUNCTION reject_remote_audit()"); }
    }
    [ServerFact]
    public async Task ProblemFiltersUseCurrentRosterSubfoldersAndServerTimeAndSurviveRestart()
    {
        var parent = await fixture.Store.CreateFolderAsync(await AdminId(), new("Problems", null), default);
        var child = await fixture.Store.CreateFolderAsync(await AdminId(), new("Child", parent), default);
        var (agent, job, client) = await Ready(); using var scope = client;
        await fixture.Store.UpdateAgentIdentityAsync(await AdminId(), agent.AgentId, new("Problem agent УНФ", child, null), default);
        var current = new MonitoredJob(job.Id, "Current failing job", "FilesAndFolders", true, "Daily", null, 1) { MaximumBackupAgeHours = 24, LocalAccessReady = false, BackupScope = new string('a', 64) };
        await fixture.Store.SaveMonitoringAsync(agent.AgentId, new(DateTimeOffset.UtcNow.AddYears(5), "stage5", [current], null, 0)
            { ExecutionMode = "Managed", ConfigurationRevision = 2, BackupExecutionAvailable = true }, default);
        await fixture.Store.SaveRunAsync(agent.AgentId, new(Guid.NewGuid(), job.Id, job.Name, DateTimeOffset.UtcNow.AddMinutes(-4), DateTimeOffset.UtcNow.AddMinutes(-3), "Succeeded", 0, false, null, [])
            { BackupScope = new string('b', 64) }, default);
        var removed = Guid.NewGuid();
        await fixture.Store.SaveRunAsync(agent.AgentId, new(Guid.NewGuid(), removed, "Removed job", DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddMinutes(-1), "Failed", 0, false, null, []), default);
        await fixture.Store.SaveRunAsync(agent.AgentId, new(Guid.NewGuid(), job.Id, job.Name, DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddMinutes(-1), "PartiallySucceeded", 1, true, null, []), default);
        var problems = await fixture.Store.ProblemsAsync(0, null, "current", parent, false, 120, default);
        Assert.Equal(4, (await fixture.Store.ProblemsAsync(0, null, "унф", parent, false, 120, default)).Total);
        Assert.Equal(4, problems.Total); Assert.All(problems.Items, p => Assert.Equal(job.Id, p.JobId));
        Assert.DoesNotContain((await fixture.Store.ProblemsAsync(0, null, null, parent, false, 120, default)).Items, p => p.JobId == removed);
        Assert.Single((await fixture.Store.ProblemsAsync(0, "Incomplete", null, parent, false, 120, default)).Items);
        Assert.Empty((await fixture.Store.ProblemsAsync(0, null, "current", null, true, 120, default)).Items);
        await fixture.ExecuteAsync("UPDATE server_monitoring SET received_utc=now()-interval '1 hour' WHERE agent_id=@id", ("id", agent.AgentId));
        Assert.Single((await fixture.Store.ProblemsAsync(0, "Offline", null, parent, false, 120, default)).Items);
        using var page = await fixture.Administrator.GetAsync($"problems?folder={parent}&kind=Incomplete&search=current");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode); var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
        Assert.Contains("Current failing job", html); Assert.Contains("Неполные копии", html); Assert.DoesNotContain("Removed job", html);
        await fixture.RestartAsync(); Assert.Single((await fixture.Store.ProblemsAsync(0, "Incomplete", null, parent, false, 120, default)).Items);
    }
    [ServerFact]
    public async Task ProtectedPlanRequiresNewCapabilityAndNeverAcceptsCredentialFields()
    {
        var (agent, job, client) = await Ready(); using var scope = client;
        var protectedJob = job with { DestinationDirectory = @"\\nas\backup\tenant", StorageKind = "NetworkShare",
            ManagedSecrets = new(true, [0]), MaximumBackupAgeHours = 24 };
        using var update = new HttpRequestMessage(HttpMethod.Put, $"api/v1/management/agents/{agent.AgentId}/configuration");
        update.Headers.IfMatch.Add(new("\"2\"")); update.Content = ServerFixture.Json(new PublishFileConfiguration(agent.AgentId, 1, [protectedJob]));
        using var published = await fixture.Administrator.SendAsync(update); Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        var saved = await published.Content.ReadAsStringAsync();
        Assert.DoesNotContain("credentialKey", saved); Assert.DoesNotContain("archivePasswordKey", saved);
        client.DefaultRequestHeaders.Remove(AgentCapabilities.Header);
        client.DefaultRequestHeaders.Add(AgentCapabilities.Header, "configuration-v1,strict-configuration-v1,managed-execution-v1");
        using (var legacy = await client.GetAsync($"api/v1/agents/{agent.AgentId}/configuration")) Assert.Equal(HttpStatusCode.Conflict, legacy.StatusCode);
        client.DefaultRequestHeaders.Remove(AgentCapabilities.Header);
        client.DefaultRequestHeaders.Add(AgentCapabilities.Header, "configuration-v1,strict-configuration-v1,managed-execution-v1,managed-secrets-v1,backup-age-v1");
        using (var ready = await client.GetAsync($"api/v1/agents/{agent.AgentId}/configuration")) Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using var leak = new HttpRequestMessage(HttpMethod.Put, $"api/v1/management/agents/{agent.AgentId}/configuration");
        leak.Headers.IfMatch.Add(new("\"3\""));
        leak.Content = new StringContent(saved.Replace("\"managedSecrets\":", "\"archivePasswordKey\":\"forbidden\",\"managedSecrets\":"), System.Text.Encoding.UTF8, "application/json");
        using var rejected = await fixture.Administrator.SendAsync(leak); Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(3, (await fixture.Store.ConfigurationAsync(agent.AgentId, default)).Revision);
    }
    [Fact]
    public void ProtectedWizardPreservesRequirementsAcrossEditCopyAndHiddenSteps()
    {
        var job = new ManagedFileJob { Id = Guid.NewGuid(), Name = "Protected", Source = @"C:\Data", DestinationDirectory = @"\\nas\backup\one", StorageKind = "NetworkShare",
            ManagedSecrets = new(true, [0, 1]), MaximumBackupAgeHours = 24,
            AdditionalDestinations = [new() { StorageKind = "NetworkShare", DestinationDirectory = @"\\nas2\backup\two" }] };
        var draft = FileJobDraft.From(job, 3); var edited = draft.Build();
        Assert.Equal(24, edited.MaximumBackupAgeHours); Assert.True(edited.ManagedSecrets!.ArchivePassword);
        Assert.Equal(new[] { 0, 1 }, edited.ManagedSecrets.NetworkDestinations);
        draft.Step = 1; var fields = draft.HiddenFields().ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal("True", fields["Model.Encrypt"]); Assert.Equal("True", fields["Model.AdditionalDestinations[0].NetworkLogin"]);
        var copied = FileJobDraft.CopyFrom(job, 3).Build(); Assert.NotEqual(job.Id, copied.Id); Assert.False(copied.Enabled);
        Assert.Equal(new[] { 0, 1 }, copied.ManagedSecrets!.NetworkDestinations);
        draft.Destination = @"D:\Local"; Assert.NotEmpty(draft.ValidateStep(3));
    }
    [ServerFact]
    public async Task SchemaSixFailureRollsBackAndUpgradePreservesEnrollmentAndHistory()
    {
        var agent = await fixture.EnrollAsync();
        await fixture.ExecuteAsync("DROP TABLE server_remote_runs; DELETE FROM server_schema WHERE version=6; CREATE FUNCTION fail_six() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.version=6 THEN RAISE EXCEPTION 'fixture'; END IF; RETURN NEW; END $$; CREATE TRIGGER fail_six BEFORE INSERT ON server_schema FOR EACH ROW EXECUTE FUNCTION fail_six()");
        try
        {
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => fixture.Store.MigrateAsync());
            await fixture.ExecuteAsync("DO $$ BEGIN IF to_regclass('server_remote_runs') IS NOT NULL OR (SELECT max(version) FROM server_schema)<>5 THEN RAISE EXCEPTION 'migration not rolled back'; END IF; END $$");
            Assert.Equal(agent.AgentId, await fixture.Store.AuthenticateAgentAsync(agent.Token, default));
        }
        finally { await fixture.ExecuteAsync("DROP TRIGGER fail_six ON server_schema; DROP FUNCTION fail_six()"); await fixture.Store.MigrateAsync(); }
        await fixture.Store.MigrateAsync(); Assert.True(await fixture.Store.ReadyAsync());
        Assert.Equal(agent.AgentId, await fixture.Store.AuthenticateAgentAsync(agent.Token, default));
    }
    private async Task<Guid> AdminId()
    {
        var user = await fixture.Store.FindUserAsync("administrator", default); return user!.Id;
    }
}
