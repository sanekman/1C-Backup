using SPBackup.Core;
using SPBackup.Core.Monitoring;
using Xunit;
using System.Net;
using System.Text.Json;

namespace SPBackup.Core.Tests;

public sealed class ExternalBackupTaskTests
{
    private readonly Guid agentId = Guid.NewGuid();
    private readonly ExternalBackupMonitor monitor = new() { Name = "Ночная копия бухгалтерии", SqlServer = "sql-host", DatabaseName = "Accounting" };
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
    private ExternalBackupReport Report(string database = "Accounting", int minutes = 0, Guid? agent = null) =>
        new(Guid.NewGuid(), agent ?? agentId, monitor.Id, null, Now, new("key-" + Guid.NewGuid(), "sql-host", database, "D",
            Now.AddMinutes(minutes - 1).UtcDateTime, Now.AddMinutes(minutes).UtcDateTime, "UTC", ExternalBackupOutcome.Completed,
            100, ["private-backup-path"], "SqlServerMsdbHistory"));

    [Fact]
    public void EmptySuccessfulPollDoesNotBecomeSuccessfulBackup()
    {
        var task = Assert.Single(ExternalBackupTask.Create([monitor], [], [new(monitor.Id, monitor.Name, Now, 0, 0)], agentId));
        Assert.Equal(monitor.Id, task.Id); Assert.Equal(monitor.Name, task.Name);
        Assert.Null(task.Latest); Assert.Equal("Копии пока не обнаружены", task.ResultDescription);
        Assert.Contains("Accounting", task.Source); Assert.Contains("60", task.ScheduleDescription);
    }

    [Fact]
    public void LatestCopyUsesCompletionTimeAndKeepsAgentAndDatabaseBoundaries()
    {
        var latest = Report(minutes: 2) with { ObservedUtc = Now.AddMinutes(-5) };
        var task = Assert.Single(ExternalBackupTask.Create([monitor],
            [Report(), latest, Report("Other", 10), Report(minutes: 20, agent: Guid.NewGuid())], [], agentId));
        Assert.Equal(latest, task.Latest);
        Assert.Equal("Копия обнаружена", task.ResultDescription);
        var changed = Assert.Single(ExternalBackupTask.Create([monitor with { DatabaseName = "NewDatabase" }], [latest], [], agentId));
        Assert.Null(changed.Latest);
    }

    [Fact]
    public void PollFailureOverridesBadgeWithoutLosingPreviousCopy()
    {
        var report = Report();
        var poll = new ExternalBackupPollResult(monitor.Id, monitor.Name, Now, 0, 0, "Нет доступа к msdb");
        var task = Assert.Single(ExternalBackupTask.Create([monitor], [report], [poll], agentId));
        Assert.Equal(report, task.Latest); Assert.Equal("Ошибка опроса", task.ResultDescription);
        Assert.Contains(poll.Error!, task.ResultDetails);
        var failed = task with { Poll = poll with { Error = null }, Latest = report with
            { Observation = report.Observation with { Outcome = ExternalBackupOutcome.Failed } } };
        Assert.Equal("Ошибка внешней копии", failed.ResultDescription);
    }

    [Fact]
    public void ObservationContractHasNoStorageAndRejectsBackupOrUnknownKindWithZeroStorages()
    {
        var task = new MonitoredJob(monitor.Id, monitor.Name, "SqlServerHistory", true, "Опрос каждые 60 с", null, 0)
            { TaskKind = "Observation" };
        MonitoringSnapshot Snapshot(MonitoredJob value) => new(Now, "test", [value], null, 0);
        Assert.True(MonitoringValidation.Valid(Snapshot(task)));
        Assert.False(MonitoringValidation.Valid(Snapshot(task with { TaskKind = "Backup" })));
        Assert.False(MonitoringValidation.Valid(Snapshot(task with { TaskKind = "Unknown" })));
        Assert.False(MonitoringValidation.Valid(Snapshot(task with { SourceKind = "FilesAndFolders" })));
        Assert.False(MonitoringValidation.Valid(Snapshot(task with { DestinationCount = 1 })));
        Assert.True(MonitoringValidation.Valid(Snapshot(task with { TaskKind = "Backup", SourceKind = "SqlServer", DestinationCount = 1 })));
    }

    [Fact]
    public async Task OlderServerKeepsSourcesAndRunDeliveryWhenItRejectsObservationJobs()
    {
        var settings = new ServerMonitoringSettings("https://server.example/", agentId, "reference");
        var transport = new LegacyServer();
        using var client = new ServerMonitoringClient(settings, transport);
        var task = new MonitoredJob(monitor.Id, monitor.Name, "SqlServerHistory", true, "Опрос", null, 0) { TaskKind = "Observation" };
        var snapshot = new MonitoringSnapshot(Now, "fixture", [task], null, 1,
            [new(monitor.Id, monitor.Name, "SqlServerHistory", true, Now, false)]);
        var run = new MonitoredRun(Guid.NewGuid(), monitor.Id, monitor.Name, Now, Now, "Succeeded", 0, false, null, []);
        await client.SendAsync(settings, "fixture-token", snapshot, [run]);
        Assert.Equal(2, transport.Snapshots.Count);
        Assert.Single(transport.Snapshots[0].Jobs);
        Assert.Empty(transport.Snapshots[1].Jobs);
        Assert.Single(transport.Snapshots[1].Sources!);
        Assert.Equal(1, transport.Runs);
    }

    private sealed class LegacyServer : HttpMessageHandler
    {
        public List<MonitoringSnapshot> Snapshots { get; } = [];
        public int Runs;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Put)
            {
                Snapshots.Add(JsonSerializer.Deserialize<MonitoringSnapshot>(await request.Content!.ReadAsStringAsync(token),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
                return new(Snapshots.Count == 1 ? HttpStatusCode.BadRequest : HttpStatusCode.NoContent);
            }
            Runs++;
            return new(HttpStatusCode.NoContent);
        }
    }
}
