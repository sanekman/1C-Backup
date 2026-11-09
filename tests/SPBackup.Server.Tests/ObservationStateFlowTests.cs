using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using SPBackup.Core;
using Xunit;
using M = SPBackup.Server.Monitoring;

namespace SPBackup.Server.Tests;

// The collector is controlled test data; polling, HTTPS, PostgreSQL persistence and Razor rendering are real.
public sealed class ObservationStateFlowTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [ServerTheory]
    [InlineData(ExternalBackupSourceKind.SqlServerHistory)]
    [InlineData(ExternalBackupSourceKind.PostgreSqlReports)]
    public async Task ChangedSourceFlowsThroughAgentAndPanelWithoutReusingHistory(ExternalBackupSourceKind kind)
    {
        var agent = await server.EnrollAsync();
        var root = Path.Combine(server.Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var settings = new AgentSettings { AgentId = agent.AgentId };
        var monitor = new ExternalBackupMonitor { Name = "Source state flow", Kind = kind,
            SqlServer = "private-old-host", DatabaseName = "private-same-database", ReportDirectory = Path.Combine(root, "old"),
            MaximumBackupAgeHours = 24 };
        var store = new ExternalBackupMonitorStore(Path.Combine(root, "external-monitors.json"));
        var outbox = new ExternalBackupOutbox(Path.Combine(root, "external-backups.json"));
        var status = new ExternalBackupPollingStatusStore(root);
        var source = new ControlledSource();
        var poller = new ExternalBackupPoller(store, outbox, settings, status, _ => source);
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(server.Root, "certificate.pfx"),
            File.ReadAllText(Path.Combine(server.Root, "certificate-password.txt")));
        await ServerMonitoringDispatcher.SaveSettingsAsync(root, new(server.Address.ToString(), agent.AgentId, "monitor",
            Convert.ToHexString(SHA256.HashData(certificate.RawData))));
        var dispatcher = new ServerMonitoringDispatcher(root, new Credentials(agent.Token));
        var zones = server.App.Services.GetRequiredService<ServerTimeZone>();
        await zones.SaveAsync("Asia/Vladivostok");
        var now = DateTimeOffset.UtcNow;
        var old = Observation("old-source", "D", now.AddMinutes(-1));
        source.Values = [old];
        await store.SaveAsync([monitor]);
        await poller.PollAsync(now); await dispatcher.PollAsync();
        var first = await Current(); Assert.Equal("CopyFound", first.ObservationStatus);
        var oldReportId = first.LatestReportId; Assert.NotNull(oldReportId);
        Assert.Contains("Копия обнаружена", await Row());

        monitor = monitor with { SqlServer = "private-new-host", ReportDirectory = Path.Combine(root, "new") };
        await store.SaveAsync([monitor]); await dispatcher.PollAsync();
        var changed = await Current(); Assert.Equal("NoCopies", changed.ObservationStatus);
        Assert.Null(changed.PolledUtc); Assert.Null(changed.LastSuccessfulUtc); Assert.Null(changed.LatestReportId);
        Assert.Null(changed.LastFullUtc); Assert.False(changed.FreshnessWarning);
        var row = await Row(); Assert.Contains("Копии пока не обнаружены", row);
        Assert.DoesNotContain(zones.Snapshot().Time(now.AddMinutes(-1)), row);

        source.Values = [];
        await poller.PollAsync(now.AddSeconds(1)); await dispatcher.PollAsync();
        var empty = await Current(); Assert.Equal("Overdue", empty.ObservationStatus); Assert.True(empty.FreshnessWarning);
        Assert.Contains("Нет свежей копии", await Row());

        source.Error = new UnauthorizedAccessException("private-password-and-path");
        await poller.PollAsync(now.AddSeconds(62)); await dispatcher.PollAsync();
        var denied = await Current(); Assert.Equal("PollFailed", denied.ObservationStatus); Assert.Equal("Access", denied.PollErrorCode);
        row = await Row(); Assert.Contains("Ошибка опроса", row); Assert.DoesNotContain("private-", row);
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(denied));

        // The new source can legitimately have older backups than the former source.
        // Its selected result must still be shown, including its evidence and failure guidance.
        source.Error = null;
        source.Values = [Observation("new-full", "D", now.AddMinutes(-10)),
            Observation("new-diff", "I", now.AddMinutes(-9)), Observation("new-log", "L", now.AddMinutes(-8)),
            Observation("new-failed", "D", now.AddMinutes(-7)) with { Outcome = ExternalBackupOutcome.Failed }];
        await poller.PollAsync(now.AddSeconds(123)); await dispatcher.PollAsync();
        var failed = await Current(); Assert.Equal("BackupFailed", failed.ObservationStatus);
        Assert.NotEqual(oldReportId, failed.LatestReportId); Assert.False(failed.Failed); Assert.Null(failed.PollErrorCode);
        row = await Row(); Assert.Contains("Ошибка внешней копии", row);
        Assert.Contains("Причины и что делать", row);
        Assert.Contains("Откройте журнал MS SQL или скрипта PostgreSQL", row);
        Assert.Contains("ExternalBackupFailed", JsonSerializer.Serialize((await server.Store.RunsAsync(agent.AgentId, 0, default)).Runs));
        Assert.Contains(zones.Snapshot().Time(now.AddMinutes(-7)), row);
        if (kind == ExternalBackupSourceKind.SqlServerHistory)
        {
            Assert.Equal(now.AddMinutes(-10).UtcDateTime, failed.LastFullUtc!.Value.UtcDateTime);
            Assert.Equal(now.AddMinutes(-9).UtcDateTime, failed.LastDifferentialUtc!.Value.UtcDateTime);
            Assert.Equal(now.AddMinutes(-8).UtcDateTime, failed.LastLogUtc!.Value.UtcDateTime);
        }

        source.Values = source.Values.Append(Observation("new-recovered", "D", now.AddMinutes(-6))).ToArray();
        var poll = Assert.Single(await poller.PollAsync(now.AddSeconds(184))); Assert.Equal(1, poll.Added);
        await dispatcher.PollAsync();
        var recovered = await Current(); Assert.Equal("CopyFound", recovered.ObservationStatus);
        row = await Row(); Assert.Contains("Копия обнаружена", row); Assert.DoesNotContain("Причины и что делать", row);
        Assert.Contains(zones.Snapshot().Time(now.AddMinutes(-6)), row);
        var before = (await outbox.ReadAsync()).Select(r => (r.ReportId, r.ObservedUtc)).ToArray();
        Assert.Equal(0, Assert.Single(await poller.PollAsync(now.AddSeconds(245))).Added);
        await dispatcher.PollAsync();
        Assert.Equal(before, (await outbox.ReadAsync()).Select(r => (r.ReportId, r.ObservedUtc)).ToArray());
        Assert.Equal(6, (await server.Store.RunsAsync(agent.AgentId, 0, default)).Runs.Count);
        Assert.Single(await server.Store.LatestRunsAsync(agent.AgentId, default));
        await server.RestartAsync();
        Assert.Equal(recovered.LatestReportId, (await Current()).LatestReportId);
        Assert.Contains(zones.Snapshot().Time(now.AddMinutes(-6)), await Row());

        ExternalBackupObservation Observation(string key, string type, DateTimeOffset finished) => new(key, "private-instance",
            monitor.DatabaseName, type, finished.AddMinutes(-1).UtcDateTime, finished.UtcDateTime, "UTC", ExternalBackupOutcome.Completed,
            4096, ["private-location"], kind == ExternalBackupSourceKind.SqlServerHistory ? "SqlServerMsdbHistory" : "PgRestoreCatalogChecked");
        async Task<M.MonitoredSource> Current() => Assert.Single((await server.Store.MonitoringAsync(agent.AgentId, default))!.Snapshot.Sources!);
        async Task<string> Row()
        {
            using var response = await server.Administrator.GetAsync($"agents/{agent.AgentId}"); response.EnsureSuccessStatusCode();
            var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
            var match = Regex.Match(html, @"<tr><td>Source state flow.*?</tr>", RegexOptions.Singleline);
            Assert.True(match.Success, "Observation task row must be rendered"); return match.Value;
        }
    }

    [ServerFact]
    public async Task SelectedReportIsAgentAndJobScopedAndDoesNotDependOnTheHistoryPage()
    {
        var agent = await server.EnrollAsync(); var other = await server.EnrollAsync();
        var jobId = Guid.NewGuid(); var selectedId = Guid.NewGuid(); var finished = DateTimeOffset.UtcNow.AddDays(-2);
        var selected = new M.MonitoredRun(selectedId, jobId, "Selected report", finished.AddMinutes(-1), finished,
            "Failed", 0, false, null, [], "SqlServerMsdbHistory")
            { Issues = [new("ExternalBackupFailed", "Error", "Source")] };
        await server.Store.SaveRunAsync(other.AgentId, selected, default);
        Assert.Empty(await server.Store.RunsByIdAsync(agent.AgentId, [selectedId], default));
        var task = new M.MonitoredJob(jobId, "Source state flow", "SqlServerHistory", true, "Polling", null, 0) { TaskKind = "Observation" };
        var state = new M.MonitoredSource(jobId, task.Name, task.SourceKind, true, DateTimeOffset.UtcNow, false)
            { ObservationStatus = "BackupFailed", LatestReportId = selectedId, LatestFinishedUtc = finished };
        await server.Store.SaveMonitoringAsync(agent.AgentId, new(DateTimeOffset.UtcNow, "test", [task], null, 0, [state]), default);
        Assert.Contains("Подробный отчёт ещё не получен сервером", await Row());
        await server.Store.SaveRunAsync(agent.AgentId, selected with { JobId = Guid.NewGuid() }, default);
        Assert.Contains("Подробный отчёт ещё не получен сервером", await Row());
        // A report from another task cannot supply the reason, even with the referenced ID.
        Assert.DoesNotContain("Причины и что делать", await Row());

        var valid = selected with { RunId = Guid.NewGuid() };
        await server.Store.SaveRunAsync(agent.AgentId, valid, default);
        for (var i = 0; i < 55; i++)
            await server.Store.SaveRunAsync(agent.AgentId, selected with { RunId = Guid.NewGuid(), FinishedUtc = finished.AddHours(1).AddMinutes(i),
                Status = "Succeeded", Issues = null }, default);
        Assert.DoesNotContain((await server.Store.RunsAsync(agent.AgentId, 0, default)).Runs, run => run.RunId == valid.RunId);
        await server.Store.SaveMonitoringAsync(agent.AgentId, new(DateTimeOffset.UtcNow, "test", [task], null, 0,
            [state with { LatestReportId = valid.RunId }]), default);
        Assert.Contains("Причины и что делать", await Row());
        Assert.DoesNotContain("Подробный отчёт ещё не получен сервером", await Row());
        Assert.Empty(await server.Store.RunsByIdAsync(agent.AgentId, [], default));
        await Assert.ThrowsAsync<ServerRequestException>(() => server.Store.RunsByIdAsync(agent.AgentId, [Guid.Empty], default));
        await Assert.ThrowsAsync<ServerRequestException>(() => server.Store.RunsByIdAsync(agent.AgentId, Enumerable.Repeat(valid.RunId, 1001).ToArray(), default));

        async Task<string> Row()
        {
            using var response = await server.Administrator.GetAsync($"agents/{agent.AgentId}"); response.EnsureSuccessStatusCode();
            return Regex.Match(WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()),
                @"<tr><td>Source state flow.*?</tr>", RegexOptions.Singleline).Value;
        }
    }

    private sealed class Credentials(string token) : IFtpCredentialStore
    {
        public Task<NetworkCredential> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(new NetworkCredential("agent", token));
    }
    private sealed class ControlledSource : IExternalBackupSource
    {
        public IReadOnlyList<ExternalBackupObservation> Values = [];
        public Exception? Error;
        public Task<IReadOnlyList<ExternalBackupObservation>> ReadAsync(ExternalBackupMonitor monitor, CancellationToken cancellationToken = default) =>
            Error is null ? Task.FromResult(Values) : Task.FromException<IReadOnlyList<ExternalBackupObservation>>(Error);
    }
}
