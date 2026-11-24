using System.Net;
using System.Text.RegularExpressions;
using SPBackup.Server;
using Xunit;

namespace SPBackup.Server.Tests;

public sealed partial class JobWizardTests
{
    private async Task<(string Path, string Html)> StorageStep(Guid agent)
    {
        var path = Url(agent);
        var step = await Post(path, await Get(path), "next", Source());
        step = await Post(path, step.Html, "next");
        Assert.Contains("3. Где хранить копии", WebUtility.HtmlDecode(step.Html));
        return (path, step.Html);
    }
    [ServerFact]
    public async Task AdditionalStorageRowsSurviveNavigationAndEditingWithoutChangingOtherJobs()
    {
        var agent = await fixture.EnrollAsync(); var other = Job("Keep other job") with { AdditionalDestinations = [new() {
            StorageKind = "NetworkShare", DestinationDirectory = @"\\old-storage\copies", KeepLast = 11 }] };
        await Seed(agent.AgentId, other);
        var (path, html) = await StorageStep(agent.AgentId);
        var step = await Post(path, html, "add-storage", new() { ["Model.Destination"] = @"D:\Copies", ["Model.Keep"] = "3" });
        step = await Post(path, step.Html, "add-storage", new() {
            ["Model.AdditionalDestinations[0].Destination"] = @"\\nas\copies", ["Model.AdditionalDestinations[0].Keep"] = "9" });
        step = await Post(path, step.Html, "next", new() { ["Model.AdditionalDestinations[1].Destination"] = @"E:\OtherCopies" });
        Assert.Contains("Хранилище 3", WebUtility.HtmlDecode(step.Html));
        Assert.Contains("как в основном (3)", WebUtility.HtmlDecode(step.Html));
        var back = await Post(path, step.Html, "back");
        Assert.Equal(@"\\nas\copies", Form(back.Html).Single(v => v.Key == "Model.AdditionalDestinations[0].Destination").Value);
        back = await Post(path, back.Html, "back"); back = await Post(path, back.Html, "next");
        Assert.Equal(@"E:\OtherCopies", Form(back.Html).Single(v => v.Key == "Model.AdditionalDestinations[1].Destination").Value);
        var review = await Post(path, back.Html, "next"); var saved = await Post(path, review.Html, "publish");
        Assert.Contains("Задание сохранено", WebUtility.HtmlDecode(saved.Html));
        var configuration = await fixture.Store.ReadFileConfigurationAsync(agent.AgentId, default);
        Assert.Equal(3, configuration.Revision); var job = configuration.Jobs.Single(j => j.Id != other.Id);
        Assert.Equal(2, job.AdditionalDestinations!.Count); Assert.Equal(9, job.AdditionalDestinations[0].KeepLast);
        Assert.Null(job.AdditionalDestinations[1].KeepLast);
        var edit = $"agents/{agent.AgentId}/jobs/{job.Id}/edit"; step = await Post(edit, await Get(edit), "next");
        step = await Post(edit, step.Html, "next");
        var formHtml = Regex.Matches(step.Html, "<form\\b[^>]*>[\\s\\S]*?</form>").Cast<Match>()
            .Select(m => m.Value).Single(f => f.Contains("value=\"job-wizard\""));
        Assert.Contains("value=\"next\"", Regex.Match(formHtml, "<button\\b[^>]*>").Value);
        step = await Post(edit, step.Html, "remove-storage:0");
        Assert.Equal(@"E:\OtherCopies", Form(step.Html).Single(v => v.Key == "Model.AdditionalDestinations[0].Destination").Value);
        Assert.Equal(2, (await fixture.Store.ReadFileConfigurationAsync(agent.AgentId, default)).Jobs.Single(j => j.Id == job.Id).AdditionalDestinations!.Count);
        step = await Post(edit, step.Html, "next"); await Post(edit, step.Html, "publish");
        configuration = await fixture.Store.ReadFileConfigurationAsync(agent.AgentId, default);
        Assert.Equal(4, configuration.Revision);
        Assert.Equal(@"E:\OtherCopies", Assert.Single(configuration.Jobs.Single(j => j.Id == job.Id).AdditionalDestinations!).DestinationDirectory);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(other), System.Text.Json.JsonSerializer.Serialize(configuration.Jobs.Single(j => j.Id == other.Id)));
    }

    [ServerFact]
    public async Task UnsafeAdditionalFoldersAndInvalidLimitsKeepTheDraftAndCannotPublish()
    {
        var agent = await fixture.EnrollAsync(); var (path, html) = await StorageStep(agent.AgentId);
        var step = await Post(path, html, "add-storage", new() { ["Model.Destination"] = @"D:\Copies", ["Model.Temporary"] = @"F:\Work" });
        foreach (var invalid in new[] { @"c:\data\company\copies", @"d:/copies/", @"D:\Copies\Nested", @"F:\Work\Nested", "relative" })
        {
            step = await Post(path, step.Html, "next", new() { ["Model.AdditionalDestinations[0].Destination"] = invalid });
            Assert.Contains("3. Где хранить копии", WebUtility.HtmlDecode(step.Html));
            Assert.Contains("Хранилище 2:", WebUtility.HtmlDecode(step.Html));
            Assert.Equal(invalid, Form(step.Html).Single(v => v.Key == "Model.AdditionalDestinations[0].Destination").Value);
        }
        step = await Post(path, step.Html, "next", new() { ["Model.AdditionalDestinations[0].Destination"] = @"E:\Safe", ["Model.AdditionalDestinations[0].Keep"] = "0" });
        Assert.Contains("от 1 до 10000", WebUtility.HtmlDecode(step.Html));
        step = await Post(path, step.Html, "remove-storage:99");
        Assert.Contains("Не удалось выбрать хранилище", WebUtility.HtmlDecode(step.Html));
        Assert.Equal(1, (await fixture.Store.ConfigurationAsync(agent.AgentId, default)).Revision);
    }

    [ServerFact]
    public async Task BrowserAcceptsEightFoldersButNeitherNinthNorForgedExtraRows()
    {
        var agent = await fixture.EnrollAsync(); var (path, html) = await StorageStep(agent.AgentId);
        var step = await Post(path, html, "add-storage", new() { ["Model.Destination"] = @"D:\Primary" });
        for (var index = 0; index < 7; index++)
            step = await Post(path, step.Html, index == 6 ? "next" : "add-storage", new() {
                [$"Model.AdditionalDestinations[{index}].Destination"] = $@"D:\Extra{index}" });
        Assert.Contains("4. Проверьте задание", WebUtility.HtmlDecode(step.Html));
        var review = step.Html; step = await Post(path, review, "back");
        step = await Post(path, step.Html, "add-storage"); Assert.Contains("максимальное число хранилищ", WebUtility.HtmlDecode(step.Html));
        var forged = await Post(path, review, "publish", new() { ["Model.AdditionalDestinations[7].Destination"] = @"D:\Ninth", ["Model.AdditionalDestinations[7].Keep"] = "5" });
        Assert.Contains("не более 8", WebUtility.HtmlDecode(forged.Html));
        Assert.Equal(1, (await fixture.Store.ConfigurationAsync(agent.AgentId, default)).Revision);
        var saved = await Post(path, review, "publish"); Assert.Contains("Задание сохранено", WebUtility.HtmlDecode(saved.Html));
        Assert.Equal(7, Assert.Single((await fixture.Store.ReadFileConfigurationAsync(agent.AgentId, default)).Jobs).AdditionalDestinations!.Count);
    }
}
