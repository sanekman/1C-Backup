using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Xunit;

namespace SPBackup.Server.Tests;

public sealed class ServerSettingsTransferTests(ServerFixture original) : IClassFixture<ServerFixture>
{
    [ServerFact]
    public async Task PortableSettingsImportIntoAFreshServerAndAssignThePlanOnlyAfterTargetedEnrollment()
    {
        var enrolled = await original.EnrollAsync();
        var parent = Guid.NewGuid();
        var child = Guid.NewGuid();
        await original.ExecuteAsync("""
            INSERT INTO server_agent_folders(id,parent_id,name,name_key)
            VALUES (@parent,NULL,'Organisation','ORGANISATION'),(@child,@parent,'Department','DEPARTMENT');
            UPDATE server_agents SET name='Backup workstation',folder_id=@child WHERE id=@agent;
            """, ("parent", parent), ("child", child), ("agent", enrolled.AgentId));
        var job = new ManagedFileJob { Id = Guid.NewGuid(), Name = "Department documents", Source = @"C:\Source", DestinationDirectory = @"D:\Copies" };
        using (var publish = new HttpRequestMessage(HttpMethod.Put, $"api/v1/management/agents/{enrolled.AgentId:D}/configuration"))
        {
            publish.Headers.IfMatch.Add(new EntityTagHeaderValue("\"1\""));
            publish.Content = ServerFixture.Json(new PublishFileConfiguration(enrolled.AgentId, 1, [job]));
            using var published = await original.Administrator.SendAsync(publish);
            Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        }
        using var zone = await original.Administrator.PostAsync("management/time-zone", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["timeZoneId"] = "Asia/Vladivostok" }));
        Assert.Equal(HttpStatusCode.Redirect, zone.StatusCode);

        using var exported = await original.Administrator.GetAsync("management/settings-transfer/export");
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        Assert.Equal("SP-Backup-server-settings.json", exported.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var json = await exported.Content.ReadAsStringAsync();
        Assert.DoesNotContain(original.Password, json);
        Assert.DoesNotContain(enrolled.Token, json);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        var document = JsonNode.Parse(json)!.AsObject();
        Assert.Equal("Asia/Vladivostok", document["panel"]!["timeZoneId"]!.GetValue<string>());
        Assert.Equal(2, document["folders"]!.AsArray().Count);
        Assert.Equal(1, document["plans"]!.AsArray().Count);
        document["panel"]!["displayName"] = "Transferred server";

        var target = new ServerFixture();
        await target.InitializeAsync();
        try
        {
            var malformed = document.DeepClone().AsObject();
            malformed["panel"]!["connectionString"] = "not portable";
            await ImportAsync(target, malformed, "invalid");
            Assert.Empty(await target.Store.FoldersAsync());
            await ImportAsync(target, document, "imported");
            await ImportAsync(target, document, "occupied");
            Assert.Equal("Organisation / Department", Assert.Single(await target.Store.FoldersAsync(), item => item.Id == child).Path);

            var display = WebUtility.HtmlDecode(await target.Administrator.GetStringAsync("settings-transfer"));
            Assert.Contains("Transferred server", display);
            Assert.Contains("Backup workstation", display);
            Assert.Contains("Organisation / Department", display);
            Assert.Contains($"connect?agentId={enrolled.AgentId:D}", display);

            var generic = await target.InviteAsync();
            using var unauthorizedAgent = target.Client();
            using var denied = await unauthorizedAgent.PostAsync("api/v1/enrollment", ServerFixture.Json(new EnrollmentRequest(
                generic.Code, enrolled.AgentId, "agent")));
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Null(await target.Store.AgentAsync(enrolled.AgentId, default));

            var newAgent = await target.EnrollAsync(enrolled.AgentId);
            Assert.NotEqual(enrolled.Token, newAgent.Token);
            var agent = await target.Store.AgentAsync(enrolled.AgentId, default);
            Assert.Equal(child, agent?.FolderId);
            Assert.Equal("Backup workstation", agent?.Name);
            var plan = await target.Store.ReadFileConfigurationAsync(enrolled.AgentId, default);
            Assert.Equal(1, plan.Revision);
            Assert.Equal(job, Assert.Single(plan.Jobs));
            Assert.Empty(await target.Store.ImportedAgentsAsync(default));
            await target.RestartAsync();
            var refreshed = JsonNode.Parse(await target.Administrator.GetStringAsync("management/settings-transfer/export"))!.AsObject();
            Assert.Equal("Transferred server", refreshed["panel"]!["displayName"]!.GetValue<string>());
            Assert.Equal("Asia/Vladivostok", refreshed["panel"]!["timeZoneId"]!.GetValue<string>());
            using var changed = await target.Administrator.PostAsync("management/time-zone", new FormUrlEncodedContent(
                new Dictionary<string, string> { ["timeZoneId"] = "Europe/Moscow" }));
            Assert.Equal(HttpStatusCode.Redirect, changed.StatusCode);
            await target.RestartAsync();
            refreshed = JsonNode.Parse(await target.Administrator.GetStringAsync("management/settings-transfer/export"))!.AsObject();
            Assert.Equal("Europe/Moscow", refreshed["panel"]!["timeZoneId"]!.GetValue<string>());
        }
        finally { await target.DisposeAsync(); }
    }

    private static async Task ImportAsync(ServerFixture target, JsonObject settings, string expected)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("true"), "confirm");
        form.Add(new StringContent(settings.ToJsonString()), "settings", "settings.json");
        using var result = await target.Administrator.PostAsync("management/settings-transfer/import", form);
        Assert.Equal(HttpStatusCode.Redirect, result.StatusCode);
        Assert.Equal("/settings-transfer?result=" + expected, result.Headers.Location?.OriginalString);
    }
}
