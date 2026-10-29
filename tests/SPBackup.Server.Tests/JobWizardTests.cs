using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using SPBackup.Server;
using Xunit;

namespace SPBackup.Server.Tests;

public sealed class JobWizardTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private static string Url(Guid id) => $"agents/{id}/jobs/new";
    private static string Attribute(string html, string name) => WebUtility.HtmlDecode(Regex.Match(html, "\\b" + name + "=\"([^\"]*)\"").Groups[1].Value);
    private static List<KeyValuePair<string, string>> Form(string html, string handler = "job-wizard")
    {
        var form = Regex.Matches(html, "<form\\b[^>]*>[\\s\\S]*?</form>").Cast<Match>()
            .Select(m => m.Value).Single(f => f.Contains("value=\"" + handler + "\""));
        var values = new List<KeyValuePair<string, string>>();
        foreach (Match input in Regex.Matches(form, "<input\\b[^>]*>"))
        {
            var name = Attribute(input.Value, "name"); if (name == "") continue;
            if (Attribute(input.Value, "type") == "checkbox" && !Regex.IsMatch(input.Value, "\\bchecked(?:[ =>])")) continue;
            values.Add(new(name, Attribute(input.Value, "value")));
        }
        foreach (Match select in Regex.Matches(form, "<select\\b([^>]*)>([\\s\\S]*?)</select>"))
        {
            var options = Regex.Matches(select.Groups[2].Value, "<option\\b[^>]*>").Cast<Match>().ToArray();
            var option = options.FirstOrDefault(o => Regex.IsMatch(o.Value, "\\bselected(?:[ =>])")) ?? options.First();
            values.Add(new(Attribute(select.Groups[1].Value, "name"), Attribute(option.Value, "value")));
        }
        Assert.Contains(values, p => p.Key == "__RequestVerificationToken" && p.Value.Length > 0);
        return values;
    }
    private async Task<string> Get(string path, HttpClient? client = null)
    { using var response = await (client ?? fixture.Administrator).GetAsync(path); response.EnsureSuccessStatusCode(); return await response.Content.ReadAsStringAsync(); }
    private async Task<(HttpStatusCode Status, string Html)> Post(string path, string html, string command,
        Dictionary<string, string>? changes = null, HttpClient? client = null, string handler = "job-wizard")
    {
        var values = Form(html, handler);
        foreach (var change in changes ?? []) { values.RemoveAll(p => p.Key == change.Key); values.Add(new(change.Key, change.Value)); }
        values.Add(new("Command", command));
        using var response = await (client ?? fixture.Administrator).PostAsync(path, new FormUrlEncodedContent(values));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
    private static Dictionary<string, string> Source(string kind = "FilesAndFolders") => new()
    { ["Model.Name"] = "Документы <script>не код</script>", ["Model.Kind"] = kind,
        ["Model.Source"] = kind == "OneCFile" ? @"C:\Bases\Company\1Cv8.1CD" : @"C:\Data\Company", ["Model.AllowOpen"] = "true" };
    private async Task<string> Review(string path, string? html = null, string kind = "FilesAndFolders")
    {
        var first = await Post(path, html ?? await Get(path), "next", Source(kind)); Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Contains("2. Когда делать копии", WebUtility.HtmlDecode(first.Html));
        var second = await Post(path, first.Html, "next", new() { ["Model.Schedule"] = "Weekly", ["Model.Enabled"] = "true",
            ["Model.TimeZone"] = "Asia/Vladivostok", ["Model.Times"] = "09:00, 23:00", ["Model.Monday"] = "false", ["Model.Wednesday"] = "true", ["Model.Friday"] = "true" });
        Assert.Equal(HttpStatusCode.OK, second.Status); Assert.Contains("3. Где хранить копии", WebUtility.HtmlDecode(second.Html));
        var third = await Post(path, second.Html, "next", new() { ["Model.Destination"] = @"\\storage\copies", ["Model.BackupMode"] = kind == "OneCFile" ? "Full" : "Incremental",
            ["Model.FullDay"] = "0", ["Model.Keep"] = "7" });
        Assert.Equal(HttpStatusCode.OK, third.Status); Assert.Contains("4. Проверьте задание", WebUtility.HtmlDecode(third.Html)); return third.Html;
    }
    private async Task Seed(Guid id, params ManagedFileJob[] jobs)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/v1/management/agents/{id}/configuration")
        { Content = new StringContent(JsonSerializer.Serialize(new PublishFileConfiguration(id, 1, jobs),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }),
            System.Text.Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        using var response = await fixture.Administrator.SendAsync(request); response.EnsureSuccessStatusCode();
    }
    private static ManagedFileJob Job(string name) => new() { Id = Guid.NewGuid(), Name = name,
        Source = @"C:\Data\Company", DestinationDirectory = @"D:\Backups", TemporaryDirectory = @"E:\Work",
        ScheduleKind = "Weekly", Enabled = true, DaysOfWeek = "Tuesday, Saturday", TimeZoneId = "Asia/Vladivostok",
        DailyAt = new TimeOnly(13, 45, 12).Add(TimeSpan.FromTicks(1234567)), AdditionalDailyTimes = [new(23, 30)],
        FileBackupMode = "Incremental", FullBackupDayOfWeek = DayOfWeek.Sunday, KeepLast = 15 };

    [ServerTheory]
    [InlineData("FilesAndFolders")]
    [InlineData("OneCFile")]
    public async Task BrowserStepsPreserveBackNavigationAndPublishAPlanWithoutClaimingAcceptance(string kind)
    {
        var agent = await fixture.EnrollAsync(); var path = Url(agent.AgentId);
        var start = await Get(path);
        var next = await Post(path, start, "next", Source(kind));
        var back = await Post(path, next.Html, "back");
        Assert.Contains("Документы <script>", WebUtility.HtmlDecode(back.Html));
        Assert.Equal(Source(kind)["Model.Source"], Form(back.Html).Single(p => p.Key == "Model.Source").Value);
        Assert.DoesNotContain("<script>не код</script>", back.Html);
        Assert.Equal(1, (await fixture.Store.ConfigurationAsync(agent.AgentId, default)).Revision);
        var review = await Review(path, back.Html, kind);
        var saved = await Post(path, review, "publish"); Assert.Equal(HttpStatusCode.OK, saved.Status);
        Assert.Contains("Задание сохранено", WebUtility.HtmlDecode(saved.Html));
        Assert.Contains("ещё не результат копирования", WebUtility.HtmlDecode(saved.Html));
        var configuration = await fixture.Store.ReadFileConfigurationAsync(agent.AgentId, default);
        Assert.Equal(2, configuration.Revision); var job = Assert.Single(configuration.Jobs);
        Assert.Equal(kind, job.Kind); Assert.Equal("NetworkShare", job.StorageKind); Assert.Equal(@"\\storage\copies", job.DestinationDirectory);
        Assert.Equal("Wednesday, Friday", job.DaysOfWeek); Assert.Equal(new TimeOnly(9, 0), job.DailyAt);
        Assert.Equal(new TimeOnly(23, 0), Assert.Single(job.AdditionalDailyTimes!)); Assert.Equal("Asia/Vladivostok", job.TimeZoneId);
        Assert.Null((await fixture.Store.AgentAsync(agent.AgentId, default))!.ReportedRevision);
        if (OperatingSystem.IsWindows())
        {
            var wire = await fixture.Store.ConfigurationAsync(agent.AgentId, default);
            var core = JsonSerializer.Deserialize<SPBackup.Core.ServerConfiguration>(wire.Document,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!;
            Assert.Empty(Assert.Single(core.Jobs).Validate());
        }
        using var list = await fixture.Administrator.GetAsync($"agents/{agent.AgentId}/jobs");
        Assert.Contains("Изменить", WebUtility.HtmlDecode(await list.Content.ReadAsStringAsync()));
    }

    [ServerFact]
    public async Task EditingRoundTripsPrecisionAndPreservesEveryOtherJob()
    {
        var agent = await fixture.EnrollAsync(); var original = Job("Edit me"); var other = Job("Keep other job");
        await Seed(agent.AgentId, original, other);
        var path = $"agents/{agent.AgentId}/jobs/{original.Id}/edit"; var html = await Get(path);
        var next = await Post(path, html, "next", new() { ["Model.Name"] = "Edited name" });
        next = await Post(path, next.Html, "next"); next = await Post(path, next.Html, "next");
        var before = await fixture.Store.ConfigurationAsync(agent.AgentId, default);
        var forged = await Post(path, next.Html, "publish", new() { ["Model.JobId"] = other.Id.ToString() });
        Assert.DoesNotContain("Задание сохранено", WebUtility.HtmlDecode(forged.Html));
        Assert.Equal(before, await fixture.Store.ConfigurationAsync(agent.AgentId, default));
        var saved = await Post(path, next.Html, "publish"); Assert.Equal(HttpStatusCode.OK, saved.Status);
        Assert.Contains("Задание сохранено", WebUtility.HtmlDecode(saved.Html));
        var configuration = await fixture.Store.ReadFileConfigurationAsync(agent.AgentId, default);
        Assert.Equal(3, configuration.Revision); Assert.Equal(2, configuration.Jobs.Count);
        Assert.Equal(JsonSerializer.Serialize(other), JsonSerializer.Serialize(configuration.Jobs.Single(j => j.Id == other.Id)));
        Assert.Equal(JsonSerializer.Serialize(original with { Name = "Edited name" }), JsonSerializer.Serialize(configuration.Jobs.Single(j => j.Id == original.Id)));
    }

    [ServerFact]
    public async Task ConflictingEditorsAndRepeatedSaveCannotOverwriteTheAcceptedServerRevision()
    {
        var agent = await fixture.EnrollAsync(); var path = Url(agent.AgentId);
        var first = await Review(path); var second = await Review(path);
        Assert.Contains("Задание сохранено", WebUtility.HtmlDecode((await Post(path, first, "publish")).Html));
        foreach (var draft in new[] { second, first })
        {
            var conflict = await Post(path, draft, "publish"); Assert.Equal(HttpStatusCode.PreconditionFailed, conflict.Status);
            Assert.Contains("Ваши значения остались", WebUtility.HtmlDecode(conflict.Html));
            Assert.Contains("Документы", WebUtility.HtmlDecode(conflict.Html));
        }
        var configuration = await fixture.Store.ReadFileConfigurationAsync(agent.AgentId, default);
        Assert.Equal(2, configuration.Revision); Assert.Single(configuration.Jobs);
        Assert.Single((await fixture.Store.AuditPageAsync(0)).Entries, e => e.AgentId == agent.AgentId && e.Event == "ConfigurationPublished");
    }

    [ServerFact]
    public async Task InvalidStepsAndCraftedFinalPostDoNotPublishOrDiscardDraftValues()
    {
        var agent = await fixture.EnrollAsync(); var path = Url(agent.AgentId); var start = await Get(path);
        var invalid = await Post(path, start, "next", new() { ["Model.Name"] = "Keep my draft", ["Model.Source"] = "relative-folder" });
        Assert.Contains("Keep my draft", invalid.Html); Assert.Contains("полный путь", WebUtility.HtmlDecode(invalid.Html));
        var step = await Post(path, invalid.Html, "next", new() { ["Model.Source"] = @"C:\Data" });
        var weekly = await Post(path, step.Html, "next", new() { ["Model.Schedule"] = "Weekly", ["Model.Monday"] = "false" });
        Assert.Contains("хотя бы один день", WebUtility.HtmlDecode(weekly.Html));
        step = await Post(path, weekly.Html, "next", new() { ["Model.Monday"] = "true" });
        var overlap = await Post(path, step.Html, "next", new() { ["Model.Destination"] = @"C:\Data\Copies" });
        Assert.Contains("должны находиться отдельно", WebUtility.HtmlDecode(overlap.Html));
        var bypass = await Post(path, start, "publish", new() { ["Model.Step"] = "4", ["Model.Source"] = @"C:\Data", ["Model.Destination"] = @"D:\Copies" });
        Assert.DoesNotContain("Задание сохранено", WebUtility.HtmlDecode(bypass.Html));
        Assert.Equal(1, (await fixture.Store.ConfigurationAsync(agent.AgentId, default)).Revision);
    }

    [ServerFact]
    public async Task AuthenticationAntiforgeryAndRevocationAlsoProtectBrowserForms()
    {
        var agent = await fixture.EnrollAsync(); var path = Url(agent.AgentId);
        using var anonymous = fixture.Client();
        using (var response = await anonymous.GetAsync(path)) Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", agent.Token);
        using (var response = await anonymous.GetAsync(path)) Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using var browser = fixture.Client(); await fixture.SignInAsync(browser);
        var page = await Get(path, browser); var fields = Form(page); fields.RemoveAll(p => p.Key == "__RequestVerificationToken");
        fields.Add(new("Command", "next"));
        using (var response = await browser.PostAsync(path, new FormUrlEncodedContent(fields))) Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var review = await Review(path);
        using (var revoked = await fixture.Administrator.PostAsync($"api/v1/management/agents/{agent.AgentId}/revoke", ServerFixture.Json(new { }))) revoked.EnsureSuccessStatusCode();
        var denied = await Post(path, review, "publish"); Assert.DoesNotContain("Задание сохранено", WebUtility.HtmlDecode(denied.Html));
        Assert.Contains("отозван", WebUtility.HtmlDecode(denied.Html));
        Assert.Equal(1, (await fixture.Store.ConfigurationAsync(agent.AgentId, default)).Revision);
    }

    [ServerFact]
    public async Task RemovalRequiresConfirmationAndDoesNotRemoveOtherJobsOrOldRevisions()
    {
        var agent = await fixture.EnrollAsync(); var first = Job("Remove me"); var second = Job("Keep me"); await Seed(agent.AgentId, first, second);
        var path = $"agents/{agent.AgentId}/jobs/{first.Id}/remove"; var html = await Get(path);
        Assert.Equal(2, (await fixture.Store.ReadFileConfigurationAsync(agent.AgentId, default)).Jobs.Count);
        var missingRevision = Form(html, "remove-job"); missingRevision.RemoveAll(p => p.Key == "Revision");
        using (var rejected = await fixture.Administrator.PostAsync(path, new FormUrlEncodedContent(missingRevision)))
            Assert.Contains("Откройте подтверждение удаления заново", WebUtility.HtmlDecode(await rejected.Content.ReadAsStringAsync()));
        Assert.Equal(2, (await fixture.Store.ReadFileConfigurationAsync(agent.AgentId, default)).Jobs.Count);
        var removed = await Post(path, html, "", handler: "remove-job"); Assert.Equal(HttpStatusCode.OK, removed.Status);
        Assert.Contains("Сохранённые копии и история не удалены", WebUtility.HtmlDecode(removed.Html));
        var configuration = await fixture.Store.ReadFileConfigurationAsync(agent.AgentId, default);
        Assert.Equal(3, configuration.Revision); Assert.Equal(second.Id, Assert.Single(configuration.Jobs).Id);
    }

    [ServerFact]
    public async Task UnsupportedStoredConfigurationIsNeverReplacedByAnEmptyWizardPlan()
    {
        var agent = await fixture.EnrollAsync();
        await fixture.ExecuteAsync("UPDATE server_configurations SET document=jsonb_set(document,'{unsupported}', 'true') WHERE agent_id=@id", ("id", agent.AgentId));
        var before = await fixture.Store.ConfigurationAsync(agent.AgentId, default);
        var page = await Get(Url(agent.AgentId)); Assert.Contains("не поддерживает", WebUtility.HtmlDecode(page));
        Assert.DoesNotContain("value=\"job-wizard\"", page);
        Assert.Equal(before, await fixture.Store.ConfigurationAsync(agent.AgentId, default));
    }

    [ServerFact]
    public async Task StaleRemovalConfirmationCannotDeleteAnotherAdministratorsEditedPlan()
    {
        var agent = await fixture.EnrollAsync(); var job = Job("Original"); await Seed(agent.AgentId, job);
        var path = $"agents/{agent.AgentId}/jobs/{job.Id}/remove"; var old = await Get(path);
        var edit = $"agents/{agent.AgentId}/jobs/{job.Id}/edit"; var form = await Get(edit);
        var next = await Post(edit, form, "next", new() { ["Model.Name"] = "Changed by other editor" });
        next = await Post(edit, next.Html, "next"); next = await Post(edit, next.Html, "next");
        Assert.Contains("Задание сохранено", WebUtility.HtmlDecode((await Post(edit, next.Html, "publish")).Html));
        var before = await fixture.Store.ConfigurationAsync(agent.AgentId, default);
        var rejected = await Post(path, old, "", handler: "remove-job"); Assert.Equal(HttpStatusCode.PreconditionFailed, rejected.Status);
        Assert.Contains("не удалено", WebUtility.HtmlDecode(rejected.Html));
        Assert.Equal(before, await fixture.Store.ConfigurationAsync(agent.AgentId, default));
    }

    [ServerFact]
    public async Task WizardFormsAndLinksWorkUnderAnHttpsPathPrefix()
    {
        var agent = await fixture.EnrollAsync();
        await using var app = fixture.Build(prefix: "/backup"); await app.StartAsync();
        using var browser = new HttpClient(fixture.Handler()) { BaseAddress = new(app.Urls.Single().TrimEnd('/') + "/") };
        await fixture.SignInAsync(browser, "backup/");
        var path = "backup/" + Url(agent.AgentId); var html = await Get(path, browser);
        Assert.Contains("/backup/\"", html);
        var next = await Post(path, html, "next", Source(), browser);
        Assert.Equal(HttpStatusCode.OK, next.Status); Assert.Contains("2. Когда делать копии", WebUtility.HtmlDecode(next.Html));
        Assert.Equal("Документы <script>не код</script>", Form(next.Html).Single(p => p.Key == "Model.Name").Value);
        Assert.Equal(1, (await fixture.Store.ConfigurationAsync(agent.AgentId, default)).Revision);
    }

    [ServerTheory]
    [InlineData("Manual")]
    [InlineData("Weekly")]
    public async Task UncheckedScheduleStaysDisabledAfterBrowserPublication(string schedule)
    {
        var agent = await fixture.EnrollAsync(); var path = Url(agent.AgentId);
        var step = await Post(path, await Get(path), "next", Source());
        step = await Post(path, step.Html, "next", new() { ["Model.Schedule"] = schedule, ["Model.Enabled"] = "false" });
        step = await Post(path, step.Html, "next", new() { ["Model.Destination"] = @"D:\Copies" });
        var saved = await Post(path, step.Html, "publish"); Assert.Contains("Задание сохранено", WebUtility.HtmlDecode(saved.Html));
        var job = Assert.Single((await fixture.Store.ReadFileConfigurationAsync(agent.AgentId, default)).Jobs);
        Assert.False(job.Enabled); Assert.Equal(schedule, job.ScheduleKind); Assert.Equal("Local", job.TimeZoneId);
    }
}
