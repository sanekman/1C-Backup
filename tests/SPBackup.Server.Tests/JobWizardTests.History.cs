using System.Net;
using SPBackup.Server;
using Xunit;

namespace SPBackup.Server.Tests;

public sealed partial class JobWizardTests
{
    [ServerFact]
    public async Task BrowserHistoryPreviewsAndRestoresDeletedJobsUnderAnHttpsPathPrefix()
    {
        var agent = await fixture.EnrollAsync(); var job = Job("Company to restore"); await Seed(agent.AgentId, job);
        var remove = $"agents/{agent.AgentId}/jobs/{job.Id}/remove";
        await Post(remove, await Get(remove), "", handler: "remove-job");
        await using var app = fixture.Build(prefix: "/backup"); await app.StartAsync();
        using var browser = new HttpClient(fixture.Handler()) { BaseAddress = new(app.Urls.Single().TrimEnd('/') + "/") };
        await fixture.SignInAsync(browser, "backup/");
        var path = $"backup/agents/{agent.AgentId}/jobs/history/2";
        var list = await Get($"backup/agents/{agent.AgentId}/jobs/history", browser);
        Assert.Contains($"href=\"agents/{agent.AgentId}/jobs/history/2\"", list);
        var html = await Get(path, browser); Assert.Contains(job.Name, html);
        Assert.Contains(job.Source, WebUtility.HtmlDecode(html)); Assert.Contains("расписание включено", WebUtility.HtmlDecode(html));
        Assert.Contains("13:45:12.1234567", html);
        Assert.Equal(3, (await fixture.Store.ConfigurationAsync(agent.AgentId, default)).Revision);
        var missing = Form(html, "restore-plan"); missing.RemoveAll(p => p.Key == "Expected");
        using (var denied = await browser.PostAsync(path, new FormUrlEncodedContent(missing)))
            Assert.Contains("подтверждение возврата заново", WebUtility.HtmlDecode(await denied.Content.ReadAsStringAsync()));
        var result = await Post(path, html, "", client: browser, handler: "restore-plan");
        Assert.Contains("новой ревизией 4", WebUtility.HtmlDecode(result.Html));
        Assert.Equal(job.Id, Assert.Single((await fixture.Store.ReadFileConfigurationAsync(agent.AgentId, default)).Jobs).Id);
        var audit = await Get("audit"); Assert.Contains("Прежний план опубликован", WebUtility.HtmlDecode(audit));
    }

    [ServerFact]
    public async Task BrowserRestoreRequiresAntiforgeryAndRejectsStaleOrRepeatedConfirmation()
    {
        var agent = await fixture.EnrollAsync(); var job = Job("Original"); await Seed(agent.AgentId, job);
        var path = $"agents/{agent.AgentId}/jobs/history/1"; var html = await Get(path);
        var fields = Form(html, "restore-plan"); fields.RemoveAll(p => p.Key == "__RequestVerificationToken");
        using var browser = fixture.Client(); await fixture.SignInAsync(browser);
        using (var denied = await browser.PostAsync(path, new FormUrlEncodedContent(fields))) Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Equal(2, (await fixture.Store.ConfigurationAsync(agent.AgentId, default)).Revision);
        var saved = await Post(path, html, "", handler: "restore-plan"); Assert.Contains("новой ревизией 3", WebUtility.HtmlDecode(saved.Html));
        var repeated = await Post(path, html, "", handler: "restore-plan"); Assert.Equal(HttpStatusCode.PreconditionFailed, repeated.Status);
        Assert.Contains("уже изменён", WebUtility.HtmlDecode(repeated.Html));
        Assert.Empty((await fixture.Store.ReadFileConfigurationAsync(agent.AgentId, default)).Jobs);
        Assert.Equal(3, (await fixture.Store.FileConfigurationHistoryAsync(agent.AgentId, 0, default)).Total);
    }
}
