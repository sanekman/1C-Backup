using System.Net;
using System.Diagnostics;
using System.Text.Json;
using SPBackup.Server;
using SPBackup.Server.Monitoring;
using Xunit;

namespace SPBackup.Server.Tests;

public sealed class AgentProblemSummaryTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    [ServerFact]
    public async Task SummaryCountsAllReasonsBeyondOnePageAndDrillsIntoOnlyTheSelectedAgent()
    {
        var problem = await fixture.EnrollAsync();
        var clean = await fixture.EnrollAsync();
        var unreported = await fixture.EnrollAsync();
        var revoked = await fixture.EnrollAsync();
        var admin = (await fixture.Store.FindUserAsync("administrator", default))!;
        await fixture.Store.UpdateAgentIdentityAsync(admin.Id, problem.AgentId, new("Office <УНФ>", null, null));
        var jobs = Enumerable.Range(0, 64).Select(i => new MonitoredJob(Guid.NewGuid(), "Needs backup " + i,
            "FilesAndFolders", true, "Daily", null, 1) { MaximumBackupAgeHours = 24 }).ToArray();
        await fixture.Store.SaveMonitoringAsync(problem.AgentId, new(DateTimeOffset.UtcNow, "summary", jobs, null, 0), default);
        await fixture.Store.SaveMonitoringAsync(clean.AgentId, new(DateTimeOffset.UtcNow, "summary", [], null, 0), default);
        var runId = Guid.NewGuid();
        await fixture.Store.SaveRunAsync(problem.AgentId, new(runId, jobs[0].JobId, jobs[0].Name,
            DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddMinutes(-1), "PartiallySucceeded", 1, true, null,
            [new(0, true, 10, false), new(1, false, 0, false)]), default);
        await fixture.Store.SaveRunAsync(problem.AgentId, new(Guid.NewGuid(), Guid.NewGuid(), "Removed job",
            DateTimeOffset.UtcNow.AddMinutes(-4), DateTimeOffset.UtcNow.AddMinutes(-3), "Failed", 0, false, null, []), default);
        await fixture.Store.RevokeAsync(admin.Id, revoked.AgentId, default);

        var summaries = await fixture.Store.ProblemSummariesAsync([problem.AgentId, clean.AgentId, unreported.AgentId, revoked.AgentId], 120, default);
        var summary = Assert.Single(summaries, s => s.AgentId == problem.AgentId);
        Assert.Equal(67, summary.Total); Assert.NotNull(summary.ObservedUtc);
        Assert.Equal(64, Assert.Single(summary.Reasons, r => r.Code == "Overdue").Count);
        Assert.Equal(0, Assert.Single(summaries, s => s.AgentId == clean.AgentId).Total);
        var pending = Assert.Single(summaries, s => s.AgentId == unreported.AgentId);
        Assert.Null(pending.ObservedUtc); Assert.Contains(pending.Reasons, r => r.Code == "Unreported");
        Assert.DoesNotContain(summaries, s => s.AgentId == revoked.AgentId);
        var selected = await fixture.Store.ProblemsAsync(0, null, null, null, false, 120, default, problem.AgentId);
        Assert.Equal(67, selected.Total); Assert.Equal(50, selected.Items.Count);
        Assert.All(selected.Items, p => Assert.Equal(problem.AgentId, p.AgentId));
        Assert.DoesNotContain(selected.Items, p => p.JobName == "Removed job");
        Assert.Equal(17, (await fixture.Store.ProblemsAsync(1, null, null, null, false, 120, default, problem.AgentId)).Items.Count);

        var grouped = await fixture.Store.ProblemsAsync(0, null, null, null, false, 120, default, problem.AgentId, groupByJob: true);
        Assert.Equal(64, grouped.PageItems);
        Assert.Equal(1, grouped.AffectedAgents);
        Assert.Equal(64, grouped.AffectedJobs);
        Assert.Equal(53, grouped.Items.Count);
        Assert.Equal(runId, Assert.Single(grouped.Items, reason => reason.Code == "Partial").RunId);
        Assert.Equal(14, (await fixture.Store.ProblemsAsync(1, null, null, null, false, 120, default, problem.AgentId, groupByJob: true)).Items.Count);
        var problems = WebUtility.HtmlDecode(await fixture.Administrator.GetStringAsync($"problems?agent={problem.AgentId}"));
        Assert.Contains("Агентов: 1 · Заданий: 64 · Причин: 67", problems);
        Assert.Contains($"?runId={runId}#run-history", problems);
        var previousView = WebUtility.HtmlDecode(await fixture.Administrator.GetStringAsync($"problems?agent={problem.AgentId}&previousView=true"));
        Assert.Contains("Агент / задание", previousView);
        Assert.DoesNotContain($"?runId={runId}#run-history", previousView);

        using var tree = await fixture.Administrator.GetAsync($"?agent={problem.AgentId}");
        var treeHtml = WebUtility.HtmlDecode(await tree.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, tree.StatusCode);
        Assert.Contains($"data-agent-id=\"{problem.AgentId}\"", treeHtml);
        Assert.Contains("Частичная доставка · 1 из 2", treeHtml);
        Assert.Contains($"agents/{problem.AgentId}#job-{jobs[0].JobId}", treeHtml);
        using var home = await fixture.Administrator.GetAsync("?view=list");
        var html = WebUtility.HtmlDecode(await home.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Contains($"problems?agent={problem.AgentId}", html); Assert.Contains("Причин: 67", html);
        Assert.Contains("По отчёту проблем не найдено.", html); Assert.Contains("Не отслеживается: доступ отозван.", html);
        using var filtered = await fixture.Administrator.GetAsync($"problems?agent={problem.AgentId}&kind=Overdue");
        html = WebUtility.HtmlDecode(await filtered.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode); Assert.Contains("Office <УНФ>", html);
        Assert.Contains($"name=\"agent\" value=\"{problem.AgentId}\"", html);
        Assert.Contains($"&agent={problem.AgentId}", html); Assert.Contains("Причин: 64", html);

        await fixture.ExecuteAsync("UPDATE server_monitoring SET received_utc=now()-interval '1 hour' WHERE agent_id=@id", ("id", problem.AgentId));
        summary = Assert.Single(await fixture.Store.ProblemSummariesAsync([problem.AgentId], 120, default));
        Assert.Equal(68, summary.Total); Assert.Contains(summary.Reasons, r => r.Code == "Offline");
        Assert.True(summary.ObservedUtc < DateTimeOffset.UtcNow.AddMinutes(-50));
        if (Environment.GetEnvironmentVariable("ONECBACKUP_OVERVIEW_BROWSER_OUTPUT") is { } output)
        {
            await fixture.Store.SaveMonitoringAsync(clean.AgentId, new(DateTimeOffset.UtcNow, "summary", [], null, 2), default);
            await fixture.Store.SaveMonitoringAsync(problem.AgentId,
                new(DateTimeOffset.UtcNow, "summary", jobs, null, 3, CollectionWarnings: 1)
                { ReportRejections = new(1, 1, 0) }, default);
            await fixture.ExecuteAsync("UPDATE server_monitoring SET received_utc=now()-interval '1 hour' WHERE agent_id=@id", ("id", problem.AgentId));
            foreach (var (name, status, warnings, destinations) in new (string, string, int, MonitoredDestination[])[]
            {
                ("Цвет ошибки", "Failed", 0, []),
                ("Цвет предупреждения", "Succeeded", 1, [new(0, true, 10, false)]),
                ("Цвет частичной доставки", "PartiallySucceeded", 0, [new(0, true, 10, false), new(1, false, 0, false)])
            })
            {
                var agent = await fixture.EnrollAsync();
                var job = new MonitoredJob(Guid.NewGuid(), name, "FilesAndFolders", true, "Daily", null, Math.Max(1, destinations.Length));
                await fixture.Store.UpdateAgentIdentityAsync(admin.Id, agent.AgentId, new(name, null, null));
                await fixture.Store.SaveMonitoringAsync(agent.AgentId, new(DateTimeOffset.UtcNow, "colors", [job], null, 0), default);
                await fixture.Store.SaveRunAsync(agent.AgentId, new(Guid.NewGuid(), job.JobId, job.Name,
                    DateTimeOffset.UtcNow.AddSeconds(-10), DateTimeOffset.UtcNow.AddSeconds(-1), status, warnings, false, null, destinations), default);
            }
            var credentials = Path.Combine(fixture.Root, "browser-password.txt");
            await File.WriteAllTextAsync(credentials, fixture.Password);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(credentials, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (!File.Exists(Path.Combine(repository.FullName, "tests/server/agent_overview_browser.py")))
                repository = repository.Parent ?? throw new InvalidOperationException("Repository not found");
            var start = new ProcessStartInfo("python3") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = repository.FullName };
            foreach (var argument in new[] { "tests/server/agent_overview_browser.py", "--url", fixture.Address.ToString(),
                "--password-file", credentials, "--agent", problem.AgentId.ToString(), "--job", jobs[0].JobId.ToString(), "--output", output })
                start.ArgumentList.Add(argument);
            using var browser = Process.Start(start)!;
            var error = browser.StandardError.ReadToEndAsync();
            var standard = browser.StandardOutput.ReadToEndAsync();
            await browser.WaitForExitAsync();
            Assert.True(browser.ExitCode == 0, await error + await standard);
            if (Environment.GetEnvironmentVariable("ONECBACKUP_DASHBOARD_SCALE_OUTPUT") is { } scaleOutput)
                await DashboardScale.MeasureAsync(fixture, scaleOutput, repository.FullName, credentials);
        }
        await fixture.RestartAsync();
        Assert.Equal(68, Assert.Single(await fixture.Store.ProblemSummariesAsync([problem.AgentId], 120, default)).Total);
    }

    [ServerFact]
    public async Task FreshReportRestoresConnectionButOnlySuccessfulRunClearsPartialDelivery()
    {
        var agent = await fixture.EnrollAsync();
        var job = new MonitoredJob(Guid.NewGuid(), "Recovery", "FilesAndFolders", true, "Daily", null, 1);
        await fixture.Store.SaveMonitoringAsync(agent.AgentId, new(DateTimeOffset.UtcNow, "test", [job], null, 0), default);
        await fixture.Store.SaveRunAsync(agent.AgentId, new(Guid.NewGuid(), job.JobId, job.Name,
            DateTimeOffset.UtcNow.AddMinutes(-4), DateTimeOffset.UtcNow.AddMinutes(-3), "PartiallySucceeded", 0, false, null, []), default);
        await fixture.ExecuteAsync("UPDATE server_monitoring SET received_utc=now()-interval '1 hour' WHERE agent_id=@id", ("id", agent.AgentId));
        var stale = Assert.Single(await fixture.Store.ProblemSummariesAsync([agent.AgentId], 120, default));
        Assert.Contains(stale.Reasons, r => r.Code == "Offline");
        Assert.Contains(stale.Reasons, r => r.Code == "Partial");
        await fixture.Store.SaveMonitoringAsync(agent.AgentId, new(DateTimeOffset.UtcNow, "test", [job], null, 0), default);
        var online = Assert.Single(await fixture.Store.ProblemSummariesAsync([agent.AgentId], 120, default));
        Assert.DoesNotContain(online.Reasons, r => r.Code == "Offline");
        Assert.Contains(online.Reasons, r => r.Code == "Partial");
        await fixture.Store.SaveRunAsync(agent.AgentId, new(Guid.NewGuid(), job.JobId, job.Name,
            DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddMinutes(-1), "Succeeded", 0, false, null, []), default);
        Assert.Equal(0, Assert.Single(await fixture.Store.ProblemSummariesAsync([agent.AgentId], 120, default)).Total);
    }

    [ServerFact]
    public async Task BackupAndObservationWithSameIdentifierKeepDistinctCausesAndHistory()
    {
        var agent = await fixture.EnrollAsync();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var backup = new MonitoredJob(id, "Local backup", "FilesAndFolders", true, "Daily", null, 1) { MaximumBackupAgeHours = 24 };
        var observation = new MonitoredSource(id, "External SQL observation", "SqlServerHistory", true, now, false)
            { ObservationStatus = "BackupFailed", LastSuccessfulUtc = now.AddMinutes(-2) };
        var failed = new MonitoredRun(Guid.NewGuid(), id, backup.Name, now.AddMinutes(-5), now.AddMinutes(-4),
            "Failed", 0, false, null, []);
        var observed = new MonitoredRun(Guid.NewGuid(), id, observation.Name, now.AddMinutes(-3), now.AddMinutes(-2),
            "Succeeded", 0, false, null, [], "SqlServerMsdbHistory");
        await fixture.Store.SaveMonitoringAsync(agent.AgentId, new(now, "scope-tests", [backup], null, 0,
            [observation with { LatestReportId = failed.RunId }]), default);
        await fixture.Store.SaveRunAsync(agent.AgentId, failed, default);
        await fixture.Store.SaveRunAsync(agent.AgentId, observed, default);

        var page = await fixture.Store.ProblemsAsync(0, null, null, null, false, 120, default, agent.AgentId, groupByJob: true);
        Assert.Equal(3, page.Total);
        Assert.Equal(2, page.PageItems);
        Assert.Equal(2, page.AffectedJobs);
        var localFailed = Assert.Single(page.Items, problem => problem.Scope == AgentProblemScope.Backup && problem.Code == "Failed");
        Assert.Equal(failed.RunId, localFailed.RunId);
        Assert.Contains(page.Items, problem => problem.Scope == AgentProblemScope.Backup && problem.Code == "Overdue");
        var externalFailed = Assert.Single(page.Items, problem => problem.Scope == AgentProblemScope.Observation);
        Assert.Equal("Failed", externalFailed.Code);
        Assert.Null(externalFailed.RunId);
        Assert.Equal(2, (await fixture.Store.LatestRunsAsync(agent.AgentId, default)).Count);
        Assert.Equal(observed.RunId, Assert.Single(await fixture.Store.LastSuccessfulRunsAsync(agent.AgentId, default)).RunId);

        var html = WebUtility.HtmlDecode(await fixture.Administrator.GetStringAsync($"problems?agent={agent.AgentId}"));
        Assert.Contains("Ошибка внешней копии", html);
        Assert.Contains($"?runId={failed.RunId}#run-history", html);
        Assert.Contains($"agents/{agent.AgentId}#job-{id}", html);
        Assert.Contains($"agents/{agent.AgentId}#observation-{id}", html);
        var detail = WebUtility.HtmlDecode(await fixture.Administrator.GetStringAsync($"agents/{agent.AgentId}"));
        Assert.Contains($"id=\"job-{id}\"", detail);
        Assert.Contains($"id=\"observation-{id}\"", detail);
        Assert.Contains("Подробный отчёт ещё не получен сервером.", detail);
        using var api = JsonDocument.Parse(await fixture.Administrator.GetStringAsync($"api/v1/management/problems?agent={agent.AgentId}"));
        var scopes = api.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("scope").GetString()).ToArray();
        Assert.Contains("Backup", scopes);
        Assert.Contains("Observation", scopes);
        if (Environment.GetEnvironmentVariable("ONECBACKUP_PROBLEM_SCOPES_BROWSER") == "1")
        {
            var repository = Environment.GetEnvironmentVariable("ONECBACKUP_TEST_REPOSITORY")
                ?? throw new InvalidOperationException("Set ONECBACKUP_TEST_REPOSITORY for the browser check.");
            var credentials = Path.Combine(fixture.Root, "problem-browser-password.txt");
            await File.WriteAllTextAsync(credentials, fixture.Password);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(credentials, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var start = new ProcessStartInfo("python3") { UseShellExecute = false, RedirectStandardError = true, WorkingDirectory = repository };
            foreach (var argument in new[] { "tests/server/problem_scopes_browser.py", "--url", fixture.Address.ToString(),
                "--password-file", credentials, "--agent", agent.AgentId.ToString(), "--task", id.ToString() })
                start.ArgumentList.Add(argument);
            using var browser = Process.Start(start)!;
            var error = browser.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try { await browser.WaitForExitAsync(timeout.Token); }
            finally { if (!browser.HasExited) browser.Kill(entireProcessTree: true); }
            Assert.True(browser.ExitCode == 0, await error);
        }
    }

    [ServerFact]
    public async Task AgentProblemFilterStillRequiresAdministratorAndRejectsInvalidIdentifiers()
    {
        var agent = await fixture.EnrollAsync();
        using var anonymous = fixture.Client();
        using var denied = await anonymous.GetAsync($"api/v1/management/problems?agent={agent.AgentId}");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var api = await fixture.Administrator.GetAsync($"api/v1/management/problems?agent={agent.AgentId}");
        Assert.Equal(HttpStatusCode.OK, api.StatusCode);
        var result = JsonSerializer.Deserialize<AgentProblemPage>(await api.Content.ReadAsStringAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.All(result.Items, p => Assert.Equal(agent.AgentId, p.AgentId));
        foreach (var value in new[] { "invalid", Guid.Empty.ToString() })
        {
            using var bad = await fixture.Administrator.GetAsync("problems?agent=" + value);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }
        Assert.Empty(await fixture.Store.ProblemSummariesAsync([], 120, default));
        await Assert.ThrowsAsync<ServerRequestException>(() => fixture.Store.ProblemSummariesAsync(Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()).ToArray(), 120, default));
    }
}
