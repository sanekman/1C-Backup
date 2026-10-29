using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SPBackup.Core;
using SPBackup.Server;
using Xunit;

namespace SPBackup.Server.Tests;

public sealed class ConfigurationPublicationTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private static ManagedFileJob Job(string name = "Company files") => new()
    {
        Id = Guid.NewGuid(), Name = name, Source = @"C:\Data\Company", DestinationDirectory = @"D:\Backups",
        ScheduleKind = "Weekly", DaysOfWeek = "Monday, Friday", TimeZoneId = "Asia/Vladivostok", Enabled = true,
        FileBackupMode = "Incremental", FullBackupDayOfWeek = DayOfWeek.Sunday, AdditionalDailyTimes = [new(12, 0)]
    };
    private static string Url(Guid id) => $"api/v1/management/agents/{id}/configuration";
    private static string Body(Guid agent, params ManagedFileJob[] jobs) => JsonSerializer.Serialize(new PublishFileConfiguration(agent, 1, jobs),
        new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(allowIntegerValues: false) } });
    private async Task<HttpResponseMessage> Put(Guid id, string json, string? etag = "\"1\"", HttpClient? client = null, bool chunked = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Url(id));
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        request.Content = chunked ? new UnknownLengthContent(Encoding.UTF8.GetBytes(json)) : new StringContent(json, Encoding.UTF8, "application/json");
        request.Content.Headers.ContentType = new("application/json");
        // Let the server reject an oversized declared body before uploading it.
        // HTTP/1.1 may otherwise close an unread oversized upload on Windows.
        if (!chunked && Encoding.UTF8.GetByteCount(json) > 1024 * 1024) request.Headers.ExpectContinue = true;
        return await (client ?? fixture.Administrator).SendAsync(request);
    }
    private async Task<int> Audits(Guid id) => (await fixture.Store.AuditPageAsync(0)).Entries.Count(e => e.AgentId == id && e.Event == "ConfigurationPublished");
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }

    [ServerFact]
    public async Task SavedRevisionIsDurableCompatibleAndDoesNotClaimAgentAcceptance()
    {
        var agent = await fixture.EnrollAsync();
        using (var read = await fixture.Administrator.GetAsync(Url(agent.AgentId)))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode); Assert.Equal("\"1\"", read.Headers.ETag!.Tag);
            Assert.True(read.Headers.CacheControl!.NoStore);
        }
        var jobs = new[] { Job(), Job("File 1C") with { Kind = "OneCFile", Source = @"\\files\bases\Company\1Cv8.1CD", FileBackupMode = "Full" } };
        using var published = await Put(agent.AgentId, Body(agent.AgentId, jobs));
        Assert.Equal(HttpStatusCode.OK, published.StatusCode); Assert.Equal("\"2\"", published.Headers.ETag!.Tag);
        var json = await published.Content.ReadAsStringAsync();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            RespectRequiredConstructorParameters = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(allowIntegerValues: false) }
        };
        var compatible = JsonSerializer.Deserialize<ServerConfiguration>(json, options)!;
        Assert.Equal(2, compatible.Revision); Assert.Equal(2, compatible.Jobs.Count);
        Assert.Equal(WeekDays.Monday | WeekDays.Friday, compatible.Jobs[0].DaysOfWeek);
        Assert.Equal(DayOfWeek.Sunday, compatible.Jobs[0].FullBackupDayOfWeek);
        Assert.Equal(OneCBackupMethod.FileArchive, compatible.Jobs[1].OneCMethod);
        if (OperatingSystem.IsWindows()) foreach (var job in compatible.Jobs) Assert.Empty(job.Validate());
        Assert.Null((await fixture.Store.AgentAsync(agent.AgentId, default))!.ReportedRevision);
        using (var legacy = fixture.Client())
        {
            legacy.DefaultRequestHeaders.Authorization = new("Bearer", agent.Token);
            using var denied = await legacy.GetAsync($"api/v1/agents/{agent.AgentId}/configuration");
            Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
            legacy.DefaultRequestHeaders.Add(SPBackup.Server.AgentCapabilities.Header, "configuration-v1,strict-configuration-v1,managed-execution-v1");
            using var offered = await legacy.GetAsync($"api/v1/agents/{agent.AgentId}/configuration");
            Assert.Equal(HttpStatusCode.OK, offered.StatusCode);
        }
        using (var retry = await Put(agent.AgentId, Body(agent.AgentId, jobs))) Assert.Equal(HttpStatusCode.PreconditionFailed, retry.StatusCode);
        await fixture.RestartAsync();
        using var restored = await fixture.Administrator.GetAsync(Url(agent.AgentId));
        Assert.Equal("\"2\"", restored.Headers.ETag!.Tag);
        Assert.Equal(compatible.AgentId, JsonNode.Parse(await restored.Content.ReadAsStringAsync())!["agentId"]!.GetValue<Guid>());
        Assert.Equal(1, await Audits(agent.AgentId));
    }

    [ServerFact]
    public async Task ConcurrentEditorsCannotOverwriteEachOtherOrCreateDuplicateAudit()
    {
        var agent = await fixture.EnrollAsync();
        var first = Put(agent.AgentId, Body(agent.AgentId, Job("First editor")));
        var second = Put(agent.AgentId, Body(agent.AgentId, Job("Second editor")));
        var results = await Task.WhenAll(first, second);
        try
        {
            Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Single(results, r => r.StatusCode == HttpStatusCode.PreconditionFailed);
            var winner = JsonNode.Parse(await results.Single(r => r.StatusCode == HttpStatusCode.OK).Content.ReadAsStringAsync())!;
            var saved = await fixture.Store.ConfigurationAsync(agent.AgentId, default);
            Assert.Equal(2, saved.Revision);
            Assert.Equal(winner["jobs"]![0]!["name"]!.GetValue<string>(), JsonNode.Parse(saved.Document)!["jobs"]![0]!["name"]!.GetValue<string>());
            Assert.Equal(1, await Audits(agent.AgentId));
        }
        finally { foreach (var result in results) result.Dispose(); }
    }

    [ServerTheory]
    [InlineData(null, 428)]
    [InlineData("*", 400)]
    [InlineData("W/\"1\"", 400)]
    [InlineData("\"1\",\"2\"", 400)]
    [InlineData("\"0\"", 400)]
    [InlineData("\"01\"", 400)]
    [InlineData("\"9223372036854775808\"", 400)]
    public async Task PublicationRequiresOneStrongRevision(string? etag, int status)
    {
        var agent = await fixture.EnrollAsync(); using var result = await Put(agent.AgentId, Body(agent.AgentId, Job()), etag);
        Assert.Equal(status, (int)result.StatusCode); Assert.Equal(1, (await fixture.Store.ConfigurationAsync(agent.AgentId, default)).Revision);
        Assert.Equal(0, await Audits(agent.AgentId));
    }

    [ServerFact]
    public async Task InvalidWholeDocumentsDoNotChangeRevisionOrLeakSourceValues()
    {
        var agent = await fixture.EnrollAsync();
        var valid = Body(agent.AgentId, Job("PRIVATE_JOB_NAME"));
        using (var baseline = await Put(agent.AgentId, valid)) Assert.Equal(HttpStatusCode.OK, baseline.StatusCode);
        var original = await fixture.Store.ConfigurationAsync(agent.AgentId, default);
        var invalid = new List<string> { "null", "{}", valid.Replace("\"protocolVersion\":1", "\"protocolVersion\":2"),
            valid.Replace("\"protocolVersion\":1", "\"protocolVersion\":1,\"protocolVersion\":1"),
            valid.Replace("\"jobs\":", "\"password\":\"PRIVATE_SECRET\",\"jobs\":"),
            valid.Replace("\"name\":", "\"archivePasswordKey\":\"PRIVATE_SECRET\",\"name\":"),
            valid.Replace("\"fullBackupDayOfWeek\":\"Sunday\"", "\"fullBackupDayOfWeek\":\"Monday, Tuesday\"") };
        foreach (var job in new[] { Job() with { Source = @"C:\Data\..\Private" }, Job() with { DestinationDirectory = @"C:\Data\Company\backups" },
            Job() with { Source = @"\\?\C:\Data" }, Job() with { Source = @"C:\NUL" }, Job() with { ScheduleKind = "Unknown" },
            Job() with { AdditionalDailyTimes = [new(23,0)] }, Job() with { Kind = "PostgreSql" }, Job() with { OneCMethod = "PlatformDump" },
            Job() with { DaysOfWeek = "None" }, Job() with { TimeZoneId = "PRIVATE_NOT_A_ZONE" }, Job() with { StorageKind = "NetworkShare" },
            Job() with { StorageKind = "NetworkShare", DestinationDirectory = "//files/backups/folder" },
            Job() with { StorageKind = "NetworkShare", DestinationDirectory = @"\\files/share/backups" },
            Job() with { FullBackupEvery = 0 }, Job() with { TemporaryDirectory = @"C:\Data\Company\temp" } }) invalid.Add(Body(agent.AgentId, job));
        var duplicated = Job(); invalid.Add(Body(agent.AgentId, duplicated, duplicated));
        foreach (var body in invalid)
        {
            using var response = await Put(agent.AgentId, body, "\"2\"");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain("PRIVATE", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(original, await fixture.Store.ConfigurationAsync(agent.AgentId, default)); Assert.Equal(1, await Audits(agent.AgentId));
    }

    [ServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedBodiesAreBoundedWithOrWithoutContentLength(bool chunked)
    {
        var agent = await fixture.EnrollAsync();
        using var response = await Put(agent.AgentId, new string(' ', 1024 * 1024 + 1), chunked: chunked);
        Assert.True(response.StatusCode == HttpStatusCode.RequestEntityTooLarge, await response.Content.ReadAsStringAsync());
        Assert.Equal(1, (await fixture.Store.ConfigurationAsync(agent.AgentId, default)).Revision);
    }

    [ServerFact]
    public async Task PublicationRequiresAdministratorCsrfCorrectAgentAndNonRevokedAccess()
    {
        var agent = await fixture.EnrollAsync(); using var client = fixture.Client();
        using (var anonymous = await Put(agent.AgentId, Body(agent.AgentId, Job()), client: client)) Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", agent.Token);
        using (var bearer = await Put(agent.AgentId, Body(agent.AgentId, Job()), client: client)) Assert.Equal(HttpStatusCode.Unauthorized, bearer.StatusCode);
        client.DefaultRequestHeaders.Authorization = null; await fixture.SignInAsync(client);
        using (var csrf = await Put(agent.AgentId, Body(agent.AgentId, Job()), client: client)) Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode);
        using (var mismatch = await Put(agent.AgentId, Body(Guid.NewGuid(), Job()))) Assert.Equal(HttpStatusCode.Forbidden, mismatch.StatusCode);
        using (var request = new HttpRequestMessage(HttpMethod.Put, Url(agent.AgentId)) { Content = new StringContent("{}") })
        {
            request.Headers.IfMatch.Add(new("\"1\""));
            using var wrongType = await fixture.Administrator.SendAsync(request);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
        }
        var absent = Guid.NewGuid();
        using (var missing = await Put(absent, Body(absent, Job()))) Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using (var revoke = await fixture.Administrator.PostAsync($"api/v1/management/agents/{agent.AgentId}/revoke", ServerFixture.Json(new { }))) revoke.EnsureSuccessStatusCode();
        using (var denied = await Put(agent.AgentId, Body(agent.AgentId, Job()))) Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        Assert.Equal(1, (await fixture.Store.ConfigurationAsync(agent.AgentId, default)).Revision); Assert.Equal(0, await Audits(agent.AgentId));
    }

    [ServerTheory]
    [InlineData("unknown")]
    [InlineData("protocol")]
    public async Task UnsupportedStoredPolicyCannotBeReadOrOverwritten(string change)
    {
        var agent = await fixture.EnrollAsync(); var stored = JsonNode.Parse((await fixture.Store.ConfigurationAsync(agent.AgentId, default)).Document)!;
        if (change == "unknown") stored["futureSecurity"] = true; else stored["protocolVersion"] = 2;
        await fixture.ExecuteAsync("UPDATE server_configurations SET document=@doc::jsonb WHERE agent_id=@id", ("doc", stored.ToJsonString()), ("id", agent.AgentId));
        var previous = await fixture.Store.ConfigurationAsync(agent.AgentId, default);
        using var read = await fixture.Administrator.GetAsync(Url(agent.AgentId)); Assert.Equal(HttpStatusCode.Conflict, read.StatusCode);
        using var write = await Put(agent.AgentId, Body(agent.AgentId, Job())); Assert.Equal(HttpStatusCode.Conflict, write.StatusCode);
        Assert.Equal(previous, await fixture.Store.ConfigurationAsync(agent.AgentId, default)); Assert.Equal(0, await Audits(agent.AgentId));
    }

    [ServerFact]
    public async Task AuditFailureRollsBackDocumentAndRevision()
    {
        var agent = await fixture.EnrollAsync(); var previous = await fixture.Store.ConfigurationAsync(agent.AgentId, default);
        await fixture.ExecuteAsync("CREATE FUNCTION reject_configuration_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.event='ConfigurationPublished' THEN RAISE EXCEPTION 'fixture'; END IF; RETURN NEW; END $$; CREATE TRIGGER reject_configuration_audit BEFORE INSERT ON server_audit FOR EACH ROW EXECUTE FUNCTION reject_configuration_audit()");
        try
        {
            using var response = await Put(agent.AgentId, Body(agent.AgentId, Job())); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(previous, await fixture.Store.ConfigurationAsync(agent.AgentId, default)); Assert.Equal(0, await Audits(agent.AgentId));
        }
        finally { await fixture.ExecuteAsync("DROP TRIGGER reject_configuration_audit ON server_audit; DROP FUNCTION reject_configuration_audit()"); }
    }

    [ServerFact]
    public async Task StaleSessionStampCannotPublishAfterSessionRotation()
    {
        var agent = await fixture.EnrollAsync();
        var id = Guid.NewGuid(); var old = Guid.NewGuid();
        await fixture.ExecuteAsync("INSERT INTO server_users(id,name,password_hash,security_stamp) VALUES(@id,'rotation-test','not-a-login',@stamp)", ("id", id), ("stamp", old));
        var connection = File.ReadAllText(Path.Combine(fixture.Root, "connection.txt"));
        await using var writer = new Npgsql.NpgsqlConnection(connection); await writer.OpenAsync();
        await using var observer = new Npgsql.NpgsqlConnection(connection); await observer.OpenAsync();
        await using var tx = await writer.BeginTransactionAsync();
        await using (var rotate = new Npgsql.NpgsqlCommand("UPDATE server_users SET security_stamp=@stamp WHERE id=@id", writer, tx))
        { rotate.Parameters.AddWithValue("stamp", Guid.NewGuid()); rotate.Parameters.AddWithValue("id", id); await rotate.ExecuteNonQueryAsync(); }
        var publish = fixture.Store.PublishFileConfigurationAsync(id, old, agent.AgentId, 1, new(agent.AgentId, 1, [Job()]), default);
        var blocked = false; var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !publish.IsCompleted)
        {
            await using var waiting = new Npgsql.NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE @owner=ANY(pg_blocking_pids(pid)) AND query LIKE 'SELECT security_stamp%'", observer);
            waiting.Parameters.AddWithValue("owner", writer.ProcessID);
            if ((long)(await waiting.ExecuteScalarAsync())! > 0) { blocked = true; break; }
            await Task.Delay(25);
        }
        await tx.CommitAsync();
        var error = await Assert.ThrowsAsync<ServerRequestException>(() => publish);
        Assert.True(blocked, "Publication must wait for the concurrent session rotation.");
        Assert.Equal(401, error.Status); Assert.Equal(0, await Audits(agent.AgentId));
        Assert.Equal(1, (await fixture.Store.ConfigurationAsync(agent.AgentId, default)).Revision);
    }

    [ServerFact]
    public async Task ExhaustedRevisionDoesNotWrapOrChangeTheDocument()
    {
        var agent = await fixture.EnrollAsync();
        var document = JsonSerializer.Serialize(new { agentId = agent.AgentId, revision = long.MaxValue, jobs = Array.Empty<object>(), protocolVersion = 1 });
        await fixture.ExecuteAsync("UPDATE server_configurations SET revision=@revision,document=@doc::jsonb WHERE agent_id=@id",
            ("revision", long.MaxValue), ("doc", document), ("id", agent.AgentId));
        var original = await fixture.Store.ConfigurationAsync(agent.AgentId, default);
        using var result = await Put(agent.AgentId, Body(agent.AgentId, Job()), "\"9223372036854775807\"");
        Assert.Equal(HttpStatusCode.Conflict, result.StatusCode);
        Assert.Contains("configuration_revision_exhausted", await result.Content.ReadAsStringAsync());
        Assert.Equal(original, await fixture.Store.ConfigurationAsync(agent.AgentId, default)); Assert.Equal(0, await Audits(agent.AgentId));
    }
}
