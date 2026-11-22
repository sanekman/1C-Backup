using System.Net;
using SPBackup.Server.Monitoring;
using Xunit;

namespace SPBackup.Server.Tests;

public sealed class OperatorTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    [ServerFact]
    public async Task OperatorSeesOnlyGrantedJobsAndLosesSessionOnScopeChange()
    {
        var visible = await fixture.EnrollAsync();
        var hidden = await fixture.EnrollAsync();
        var visibleJob = Job("Granted files");
        var hiddenJob = Job("Same agent private files");
        var otherJob = Job("Other agent private files");
        await fixture.Store.SaveMonitoringAsync(visible.AgentId, new(DateTimeOffset.UtcNow, "fixture", [visibleJob, hiddenJob], null, 0), default);
        await fixture.Store.SaveMonitoringAsync(hidden.AgentId, new(DateTimeOffset.UtcNow, "fixture", [otherJob], null, 0), default);
        var allowedParent = Guid.NewGuid();
        var allowedFolder = Guid.NewGuid();
        var privateFolder = Guid.NewGuid();
        await fixture.ExecuteAsync("""
            INSERT INTO server_agent_folders(id,parent_id,name,name_key) VALUES
                (@parent,NULL,'Shared branch','shared branch'),
                (@folder,@parent,'Shared leaf','shared leaf'),
                (@private,NULL,'Hidden branch','hidden branch');
            UPDATE server_agents SET folder_id=@folder WHERE id=@visible;
            UPDATE server_agents SET folder_id=@private WHERE id=@hidden;
            """, ("parent", allowedParent), ("folder", allowedFolder), ("private", privateFolder),
            ("visible", visible.AgentId), ("hidden", hidden.AgentId));
        var visibleRun = Run(visibleJob, 256);
        var hiddenRun = Run(hiddenJob, 128);
        var otherRun = Run(otherJob, 64);
        await fixture.Store.SaveRunAsync(visible.AgentId, visibleRun, default);
        await fixture.Store.SaveRunAsync(visible.AgentId, hiddenRun, default);
        await fixture.Store.SaveRunAsync(hidden.AgentId, otherRun, default);
        await fixture.Store.SaveMonitoringAsync(visible.AgentId, new(DateTimeOffset.UtcNow, "fixture", [hiddenJob], null, 0), default);
        var secret = Guid.NewGuid().ToString("N");
        using var create = await fixture.Administrator.PostAsync("management/operators", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["name"] = "observer", ["password"] = secret, ["enabled"] = "true", ["grant"] = $"{visible.AgentId:D}:{visibleJob.JobId:D}"
        }));
        Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
        using var user = fixture.Client();
        await fixture.SignInAsync(user, password: secret, name: "observer");
        var page = WebUtility.HtmlDecode(await user.GetStringAsync("operator"));
        Assert.Contains(visibleJob.Name, page);
        Assert.DoesNotContain(hiddenJob.Name, page);
        Assert.DoesNotContain(otherJob.Name, page);
        Assert.DoesNotContain(secret, page);
        Assert.DoesNotContain(visible.Token, page);
        Assert.Contains("Shared branch", page);
        Assert.Contains("Shared leaf", page);
        Assert.DoesNotContain("Hidden branch", page);
        var folderPage = await user.GetStringAsync($"operator?folder={allowedParent:D}");
        Assert.Contains(visibleJob.Name, folderPage);
        using (var privateBranch = await user.GetAsync($"operator?folder={privateFolder:D}"))
            Assert.Equal(HttpStatusCode.NotFound, privateBranch.StatusCode);
        var history = $"operator/agents/{visible.AgentId:D}/jobs/{visibleJob.JobId:D}";
        var selected = WebUtility.HtmlDecode(await user.GetStringAsync($"{history}?run={visibleRun.RunId:D}"));
        Assert.Contains(visibleJob.Name, selected);
        Assert.Contains("Хранилище 1 · доставлено", selected);
        Assert.DoesNotContain(hiddenJob.Name, selected);
        foreach (var path in new[]
        {
            $"{history}?run={hiddenRun.RunId:D}",
            $"operator/agents/{visible.AgentId:D}/jobs/{hiddenJob.JobId:D}",
            $"operator/agents/{hidden.AgentId:D}/jobs/{visibleJob.JobId:D}",
            $"operator/agents/{hidden.AgentId:D}/jobs/{otherJob.JobId:D}?run={otherRun.RunId:D}"
        })
        {
            using var denied = await user.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            Assert.DoesNotContain("private files", await denied.Content.ReadAsStringAsync());
        }
        foreach (var path in new[] { "operators", "settings", "problems", $"agents/{visible.AgentId}", $"api/v1/management/agents/{hidden.AgentId}/monitoring" })
        {
            using var forbidden = await user.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }
        using (var mutate = await user.PostAsync("management/operators", ServerFixture.Json(new { })))
            Assert.Equal(HttpStatusCode.Forbidden, mutate.StatusCode);
        var search = await user.GetStringAsync("operator?search=private");
        Assert.DoesNotContain(otherJob.Name, search);
        Assert.DoesNotContain(hiddenJob.Name, search);

        await fixture.ExecuteAsync("UPDATE server_agents SET folder_id=NULL WHERE id=@agent", ("agent", visible.AgentId));
        Assert.Contains(visibleJob.Name, await user.GetStringAsync("operator?folder=root"));
        Assert.DoesNotContain("Shared branch", await user.GetStringAsync("operator"));

        using var second = fixture.Client();
        var anotherSecret = Guid.NewGuid().ToString("N");
        using var createSecond = await fixture.Administrator.PostAsync("management/operators", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["name"] = "observer-second", ["password"] = anotherSecret, ["enabled"] = "true",
            ["grant"] = $"{visible.AgentId:D}:{hiddenJob.JobId:D}"
        }));
        Assert.Equal(HttpStatusCode.Redirect, createSecond.StatusCode);
        await fixture.SignInAsync(second, password: anotherSecret, name: "observer-second");
        Assert.Contains(hiddenJob.Name, await second.GetStringAsync("operator"));
        using (var denied = await second.GetAsync(history)) Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        var account = Assert.Single(await fixture.Store.OperatorsAsync(default), value => value.Name == "observer");
        using var edit = await fixture.Administrator.PostAsync("management/operators", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = account.Id.ToString("D"), ["stamp"] = account.Stamp.ToString("D"), ["enabled"] = "true",
            ["grant"] = $"{hidden.AgentId:D}:{Guid.Empty:D}"
        }));
        Assert.Equal(HttpStatusCode.Redirect, edit.StatusCode);
        using (var stale = await user.GetAsync("operator")) Assert.Equal(HttpStatusCode.Redirect, stale.StatusCode);
        await fixture.SignInAsync(user, password: secret, name: "observer");
        page = WebUtility.HtmlDecode(await user.GetStringAsync("operator"));
        Assert.Contains(otherJob.Name, page);
        Assert.DoesNotContain(visibleJob.Name, page);
    }

    private static MonitoredRun Run(MonitoredJob job, int bytes)
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), job.JobId, job.Name, now.AddSeconds(-10), now, "Succeeded", 0, false, "Full",
            [new(0, true, bytes, false)]);
    }

    private static MonitoredJob Job(string name) => new(Guid.NewGuid(), name, "FilesAndFolders", true, "Daily", null, 1);
}
