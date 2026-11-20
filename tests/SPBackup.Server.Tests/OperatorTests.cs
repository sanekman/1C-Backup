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

        var account = Assert.Single(await fixture.Store.OperatorsAsync(default));
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

    private static MonitoredJob Job(string name) => new(Guid.NewGuid(), name, "FilesAndFolders", true, "Daily", null, 1);
}
