using System.Net;
using System.Text.Json;
using SPBackup.Server;
using Xunit;
using M = SPBackup.Server.Monitoring;

namespace SPBackup.Server.Tests;

public sealed class RunIssueTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture server;
    public RunIssueTests(ServerFixture server) => this.server = server;
    private static M.MonitoredRun Report() => new(Guid.NewGuid(), Guid.NewGuid(), "Файлы", DateTimeOffset.UtcNow.AddMinutes(-1),
        DateTimeOffset.UtcNow, "PartiallySucceeded", 1, false, "Incremental", [new(0, true, 256, false), new(1, false, 0, false)])
        { Issues = [new("NetworkLogonFailed", "Error", "Storage", 1, 1326), new("RetentionChainPreserved", "Warning", "Retention", 0)] };

    [ServerFact]
    public async Task ReasonsTravelThroughHttpsSurviveRestartAndAppearInHistoryAndLatestResult()
    {
        var agent = await server.EnrollAsync();
        using var client = server.Client(); client.DefaultRequestHeaders.Authorization = new("Bearer", agent.Token);
        var run = Report();
        var path = $"api/v1/agents/{agent.AgentId}/runs";
        using var response = await client.PostAsync(path, ServerFixture.Json(run)); Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var retry = await client.PostAsync(path, ServerFixture.Json(run)); Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        await server.Store.SaveMonitoringAsync(agent.AgentId, new(DateTimeOffset.UtcNow, "test", [new(run.JobId, "Файлы", "FilesAndFolders", true, "Daily", null, 2)], null, 0), default);
        await server.RestartAsync();
        var saved = Assert.Single((await server.Store.RunsAsync(agent.AgentId, 0, default)).Runs);
        Assert.Equal(JsonSerializer.Serialize(run.Issues), JsonSerializer.Serialize(saved.Issues));
        using var page = await server.Administrator.GetAsync($"agents/{agent.AgentId}"); page.EnsureSuccessStatusCode();
        var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
        Assert.Contains("Причины и что делать", html); Assert.Contains("отклонило логин или пароль", html);
        Assert.Contains("Хранилище 2", html); Assert.Contains("1326", html); Assert.Contains("сверх лимита для восстановления", html);
        Assert.Contains("Не удаляйте части цепочки вручную", html);
    }

    [ServerFact]
    public async Task LegacyReportsRemainReadableWithoutInventedReasons()
    {
        var agent = await server.EnrollAsync(); var report = Report() with { Issues = null };
        await server.Store.SaveRunAsync(agent.AgentId, report, default);
        using var page = await server.Administrator.GetAsync($"agents/{agent.AgentId}"); page.EnsureSuccessStatusCode();
        var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
        Assert.Contains("Подробные причины не переданы агентом", html);
        Assert.DoesNotContain("отклонило логин или пароль", html);
    }

    [ServerFact]
    public async Task ArbitraryTextInvalidScopesAndUnboundedCollectionsAreRejected()
    {
        var agent = await server.EnrollAsync();
        using var client = server.Client(); client.DefaultRequestHeaders.Authorization = new("Bearer", agent.Token);
        var invalid = new M.MonitoredIssue[] {
            new("password=secret", "Error", "Storage"), new("AccessDenied", "secret", "Storage"),
            new("AccessDenied", "Error", "private-path"), new("AccessDenied", "Error", "Storage", -1),
            new("AccessDenied", "Error", "Storage", 100), new("AccessDenied", "Error", "Storage", NativeCode: -1)
        };
        foreach (var issue in invalid)
        {
            using var response = await client.PostAsync($"api/v1/agents/{agent.AgentId}/runs", ServerFixture.Json(Report() with { Issues = [issue] }));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var excessive = await client.PostAsync($"api/v1/agents/{agent.AgentId}/runs", ServerFixture.Json(Report() with { Issues = Enumerable.Repeat(invalid[0] with { Code = "AccessDenied" }, 257).ToArray() }));
        Assert.Equal(HttpStatusCode.BadRequest, excessive.StatusCode);
        Assert.Empty((await server.Store.RunsAsync(agent.AgentId, 0, default)).Runs);
    }

    [Fact]
    public void EveryAllowedReasonHasHumanReadableGuidance()
    {
        var codes = new[] { "AccessDenied", "NetworkLogonFailed", "NetworkCredentialConflict", "NetworkUnavailable", "DiskFull", "FileNotFound", "FileBusy", "Timeout", "TlsFailed", "HttpFailed", "ExternalBackupFailed", "BackupFailed", "SourceIncomplete", "OneCOpenDatabase", "RetentionChainPreserved", "RetentionIncompletePreserved", "RetentionSkippedIncomplete", "RetentionFailed", "SnapshotIndexNotSaved", "WorkCleanupFailed", "Cancelled", "Interrupted", "AdditionalWarnings" };
        foreach (var code in codes)
        {
            var issue = new M.MonitoredIssue(code, "Error", "Backup");
            Assert.True(M.MonitoredIssueCodes.Valid(issue));
            Assert.NotEmpty(RunIssueText.Title(issue)); Assert.NotEmpty(RunIssueText.Advice(issue));
        }
    }
}
