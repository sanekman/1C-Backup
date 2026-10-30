using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SPBackup.Server;
using Xunit;

namespace SPBackup.Server.Tests;

public sealed class MultipleFileDestinationsTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private static ManagedFileJob Job => new() { Id = Guid.NewGuid(), Name = "Independent storage limits", Source = @"C:\Data",
        DestinationDirectory = @"D:\Primary", TemporaryDirectory = @"C:\Work", KeepLast = 3,
        AdditionalDestinations = [new() { StorageKind = "NetworkShare", DestinationDirectory = @"\\nas\copies", KeepLast = 11 },
            new() { StorageKind = "LocalDirectory", DestinationDirectory = @"E:\Copies" }] };
    private static JsonSerializerOptions Json => new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private async Task<HttpResponseMessage> Put(Guid id, string body, long revision = 1)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/v1/management/agents/{id}/configuration")
        { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.IfMatch.Add(new($"\"{revision}\"")); return await fixture.Administrator.SendAsync(request);
    }
    private static string Body(Guid id, ManagedFileJob job) => JsonSerializer.Serialize(new PublishFileConfiguration(id, 1, [job]), Json);
    [ServerFact]
    public async Task EightLocalAndUncDestinationsPersistAndRemainCompatibleWithExistingManagedAgents()
    {
        var agent = await fixture.EnrollAsync(); var job = Job with { AdditionalDestinations = Enumerable.Range(0, 7).Select(i => new ManagedFileDestination {
            StorageKind = i == 0 ? "NetworkShare" : "LocalDirectory", DestinationDirectory = i == 0 ? @"\\nas\copies" : $@"E:\Copies{i}", KeepLast = i == 1 ? null : i + 1 }).ToArray() };
        using (var saved = await Put(agent.AgentId, Body(agent.AgentId, job))) Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        await fixture.RestartAsync();
        var restored = Assert.Single((await fixture.Store.ReadFileConfigurationAsync(agent.AgentId, default)).Jobs);
        Assert.Equal(JsonSerializer.Serialize(job, Json), JsonSerializer.Serialize(restored, Json));
        using var client = fixture.Client(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", agent.Token);
        client.DefaultRequestHeaders.Add(SPBackup.Server.AgentCapabilities.Header, "configuration-v1,strict-configuration-v1,managed-execution-v1");
        using var fetched = await client.GetAsync($"api/v1/agents/{agent.AgentId}/configuration"); fetched.EnsureSuccessStatusCode();
        var core = JsonSerializer.Deserialize<SPBackup.Core.ServerConfiguration>(await fetched.Content.ReadAsStringAsync(), Json)!;
        var selected = Assert.Single(core.Jobs); Assert.Equal(8, selected.StorageJobs.Count);
        Assert.Equal(3, selected.StorageJobs[0].KeepLast); Assert.Equal(1, selected.StorageJobs[1].KeepLast);
        Assert.Equal(3, selected.StorageJobs[2].KeepLast); Assert.Equal(7, selected.StorageJobs[7].KeepLast);
        if (OperatingSystem.IsWindows()) Assert.Empty(selected.Validate());
        using var panel = await fixture.Administrator.GetAsync($"agents/{agent.AgentId}/jobs");
        Assert.Contains("Хранилище 8", WebUtility.HtmlDecode(await panel.Content.ReadAsStringAsync()));
    }

    [ServerFact]
    public async Task UnsafeOrSecretBearingNestedDestinationsNeverReplaceTheStoredRevision()
    {
        var agent = await fixture.EnrollAsync(); var job = Job;
        using (var saved = await Put(agent.AgentId, Body(agent.AgentId, job))) saved.EnsureSuccessStatusCode();
        var before = await fixture.Store.ConfigurationAsync(agent.AgentId, default);
        var invalid = new List<string>();
        foreach (var row in new ManagedFileDestination?[] { null,
            new() { StorageKind = "LocalDirectory", DestinationDirectory = "relative" },
            new() { StorageKind = "LocalDirectory", DestinationDirectory = @"c:\data\copies" },
            new() { StorageKind = "LocalDirectory", DestinationDirectory = @"d:/primary/" },
            new() { StorageKind = "LocalDirectory", DestinationDirectory = @"D:\Primary\Nested" },
            new() { StorageKind = "LocalDirectory", DestinationDirectory = @"C:\Work\Nested" },
            new() { StorageKind = "LocalDirectory", DestinationDirectory = @"\\nas\copies" },
            new() { StorageKind = "NetworkShare", DestinationDirectory = @"E:\Copies" },
            new() { StorageKind = "Ftp", DestinationDirectory = "/copies" },
            new() { StorageKind = "LocalDirectory", DestinationDirectory = @"E:\Copies", KeepLast = 0 },
            new() { StorageKind = "LocalDirectory", DestinationDirectory = @"E:\Copies", KeepLast = 10001 } })
            invalid.Add(Body(agent.AgentId, job with { AdditionalDestinations = [row!] }));
        invalid.Add(Body(agent.AgentId, job with { AdditionalDestinations = Enumerable.Range(0, 8).Select(i => new ManagedFileDestination {
            StorageKind = "LocalDirectory", DestinationDirectory = $@"E:\Copies{i}" }).ToArray() }));
        var baseline = JsonNode.Parse(Body(agent.AgentId, job))!;
        foreach (var change in new[] { "unknown", "missing", "nested-overlap" })
        {
            var document = baseline.DeepClone(); var rows = document["jobs"]![0]!["additionalDestinations"]!.AsArray();
            if (change == "unknown") rows[0]!["credentialKey"] = "PRIVATE_SECRET";
            if (change == "missing") rows[0]!.AsObject().Remove("storageKind");
            if (change == "nested-overlap") rows[1]!["destinationDirectory"] = @"\\NAS\copies\Nested";
            invalid.Add(document.ToJsonString());
        }
        foreach (var body in invalid)
        {
            using var denied = await Put(agent.AgentId, body, 2); Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
            Assert.DoesNotContain("PRIVATE_SECRET", await denied.Content.ReadAsStringAsync());
        }
        Assert.Equal(before, await fixture.Store.ConfigurationAsync(agent.AgentId, default));
        Assert.Single((await fixture.Store.AuditPageAsync(0)).Entries, e => e.AgentId == agent.AgentId && e.Event == "ConfigurationPublished");
    }
}
