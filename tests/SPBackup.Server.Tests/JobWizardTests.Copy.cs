using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using SPBackup.Server;
using Xunit;

namespace SPBackup.Server.Tests;

public sealed partial class JobWizardTests
{
    [ServerTheory]
    [InlineData("FilesAndFolders", @"C:\Документы\Бухгалтерия\", "Бухгалтерия")]
    [InlineData("FilesAndFolders", @"\\storage\Документы", "Документы")]
    [InlineData("FilesAndFolders", @"C:\Данные\отчёт.xlsx", "отчёт.xlsx")]
    [InlineData("OneCFile", "C:/Bases/Торговля/1cv8.1cd", "Торговля")]
    [InlineData("OneCFile", @"\\server\Bases\Склад\1Cv8.1CD", "Склад")]
    [InlineData("FilesAndFolders", @"C:\", "Диск C")]
    public async Task BrowserSuggestsWindowsSourceNamesWithoutUsingTheServersFilesystem(string kind, string source, string expected)
    {
        var agent = await fixture.EnrollAsync(); var path = Url(agent.AgentId);
        var step = await Post(path, await Get(path), "next", new() {
            ["Model.Name"] = "", ["Model.Kind"] = kind, ["Model.Source"] = source });
        Assert.Contains("2. Когда делать копии", WebUtility.HtmlDecode(step.Html));
        Assert.Equal(expected, Form(step.Html).Single(v => v.Key == "Model.Name").Value);
        var back = await Post(path, step.Html, "back");
        step = await Post(path, back.Html, "next", new() { ["Model.Source"] = @"C:\Bases\Новая база", ["Model.Kind"] = "OneCFile" });
        Assert.Equal("Новая база", Form(step.Html).Single(v => v.Key == "Model.Name").Value);
        Assert.Equal(1, (await fixture.Store.ConfigurationAsync(agent.AgentId, default)).Revision);
    }

    [ServerFact]
    public async Task CopyPreservesAllSettingsWithANewIdAndDisabledScheduleAndNeverMutatesTheTemplate()
    {
        var agent = await fixture.EnrollAsync(); var original = Job("Custom original name") with {
            AdditionalDestinations = [new() { StorageKind = "NetworkShare", DestinationDirectory = @"\\storage\copies", KeepLast = 30 },
                new() { StorageKind = "LocalDirectory", DestinationDirectory = @"F:\Copies" }] };
        var other = Job("Other job"); await Seed(agent.AgentId, original, other);
        var path = $"agents/{agent.AgentId}/jobs/{original.Id}/copy";
        var before = await fixture.Store.ConfigurationAsync(agent.AgentId, default);
        var html = await Get(path); var fields = Form(html);
        Assert.Equal(before, await fixture.Store.ConfigurationAsync(agent.AgentId, default));
        var newId = Guid.Parse(fields.Single(p => p.Key == "Model.JobId").Value); Assert.NotEqual(original.Id, newId);
        Assert.Equal("Company", fields.Single(p => p.Key == "Model.Name").Value);
        Assert.Equal("False", fields.Single(p => p.Key == "Model.Enabled").Value);
        var step = await Post(path, html, "next", new() { ["Model.Source"] = @"C:\Data\New company" });
        step = await Post(path, step.Html, "next"); step = await Post(path, step.Html, "next");
        Assert.Contains("4. Проверьте задание", WebUtility.HtmlDecode(step.Html));
        var forged = await Post(path, step.Html, "publish", new() { ["Model.JobId"] = original.Id.ToString() });
        Assert.DoesNotContain("Задание сохранено", WebUtility.HtmlDecode(forged.Html));
        Assert.Equal(before, await fixture.Store.ConfigurationAsync(agent.AgentId, default));
        var saved = await Post(path, step.Html, "publish"); Assert.Contains("Задание сохранено", WebUtility.HtmlDecode(saved.Html));
        var plan = await fixture.Store.ReadFileConfigurationAsync(agent.AgentId, default);
        Assert.Equal(3, plan.Revision); Assert.Equal(3, plan.Jobs.Count);
        foreach (var unchanged in new[] { original, other })
            Assert.Equal(JsonSerializer.Serialize(unchanged), JsonSerializer.Serialize(plan.Jobs.Single(j => j.Id == unchanged.Id)));
        var expected = original with { Id = newId, Name = "New company", Source = @"C:\Data\New company", Enabled = false };
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(plan.Jobs.Single(j => j.Id == newId)));
        var repeated = await Post(path, step.Html, "publish"); Assert.Equal(HttpStatusCode.PreconditionFailed, repeated.Status);
        Assert.Equal(2, (await fixture.Store.AuditPageAsync(0)).Entries.Count(e => e.AgentId == agent.AgentId && e.Event == "ConfigurationPublished"));
    }

    [ServerFact]
    public async Task CustomNamesSurviveSourceChangesAndClearingTheNameRestoresAutomaticNaming()
    {
        var agent = await fixture.EnrollAsync(); var original = Job("Название пользователя"); await Seed(agent.AgentId, original);
        var edit = $"agents/{agent.AgentId}/jobs/{original.Id}/edit";
        var step = await Post(edit, await Get(edit), "next", new() { ["Model.Source"] = @"C:\Data\Another" });
        Assert.Equal(original.Name, Form(step.Html).Single(p => p.Key == "Model.Name").Value);
        var back = await Post(edit, step.Html, "back");
        step = await Post(edit, back.Html, "next", new() { ["Model.Name"] = "" });
        Assert.Equal("Another", Form(step.Html).Single(p => p.Key == "Model.Name").Value);
        back = await Post(edit, step.Html, "back");
        step = await Post(edit, back.Html, "next", new() { ["Model.Name"] = "My own name", ["Model.Source"] = @"C:\Data\Third" });
        back = await Post(edit, step.Html, "back");
        step = await Post(edit, back.Html, "next", new() { ["Model.Source"] = @"C:\Data\Fourth" });
        Assert.Equal("My own name", Form(step.Html).Single(p => p.Key == "Model.Name").Value);
        Assert.Equal(2, (await fixture.Store.ConfigurationAsync(agent.AgentId, default)).Revision);
    }

    [ServerFact]
    public async Task CopyCannotOverwriteAConcurrentRevisionOrUseAMissingOrDifferentAgentsTemplate()
    {
        var agent = await fixture.EnrollAsync(); var original = Job("Template"); await Seed(agent.AgentId, original);
        var path = $"agents/{agent.AgentId}/jobs/{original.Id}/copy"; var html = await Get(path);
        var edit = $"agents/{agent.AgentId}/jobs/{original.Id}/edit";
        var step = await Post(edit, await Get(edit), "next", new() { ["Model.Name"] = "Changed elsewhere" });
        step = await Post(edit, step.Html, "next"); step = await Post(edit, step.Html, "next");
        await Post(edit, step.Html, "publish"); var before = await fixture.Store.ConfigurationAsync(agent.AgentId, default);
        var conflict = await Post(path, html, "next"); Assert.Equal(HttpStatusCode.PreconditionFailed, conflict.Status);
        Assert.Equal(before, await fixture.Store.ConfigurationAsync(agent.AgentId, default));
        var other = await fixture.EnrollAsync();
        foreach (var missing in new[] { $"agents/{agent.AgentId}/jobs/{Guid.NewGuid()}/copy", $"agents/{other.AgentId}/jobs/{original.Id}/copy" })
        { using var response = await fixture.Administrator.GetAsync(missing); Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); }
    }

    [ServerFact]
    public async Task CopyLinksWorkUnderAPathPrefixAndRequireAnAdministratorAntiforgeryAndActiveAgent()
    {
        var agent = await fixture.EnrollAsync(); var original = Job("Template"); await Seed(agent.AgentId, original);
        await using var app = fixture.Build(prefix: "/backup"); await app.StartAsync();
        using var browser = new HttpClient(fixture.Handler()) { BaseAddress = new(app.Urls.Single().TrimEnd('/') + "/") };
        await fixture.SignInAsync(browser, "backup/");
        var path = $"backup/agents/{agent.AgentId}/jobs/{original.Id}/copy";
        var list = await Get($"backup/agents/{agent.AgentId}/jobs", browser);
        Assert.Contains($"href=\"agents/{agent.AgentId}/jobs/{original.Id}/copy\"", list);
        var html = await Get(path, browser); var step = await Post(path, html, "next", new() { ["Model.Source"] = @"C:\Bases\Next" }, browser);
        Assert.Equal("Next", Form(step.Html).Single(p => p.Key == "Model.Name").Value);
        using var anonymous = fixture.Client();
        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", agent.Token);
        using (var denied = await anonymous.GetAsync(path["backup/".Length..])) Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        var fields = Form(html); fields.RemoveAll(p => p.Key == "__RequestVerificationToken"); fields.Add(new("Command", "next"));
        using (var denied = await browser.PostAsync(path, new FormUrlEncodedContent(fields))) Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        using (var revoked = await fixture.Administrator.PostAsync($"api/v1/management/agents/{agent.AgentId}/revoke", ServerFixture.Json(new { }))) revoked.EnsureSuccessStatusCode();
        var rejected = await Post(path, html, "next", client: browser);
        Assert.Contains("отозван", WebUtility.HtmlDecode(rejected.Html));
        Assert.DoesNotContain("name=\"Model.Source\"", rejected.Html);
        Assert.DoesNotContain("value=\"publish\"", rejected.Html);
        Assert.Equal(2, (await fixture.Store.ConfigurationAsync(agent.AgentId, default)).Revision);
    }
}
