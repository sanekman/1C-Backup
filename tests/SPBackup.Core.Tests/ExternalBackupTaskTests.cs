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
        Assert.Null(changed.LatestFinishedUtc);
        Assert.Null(changed.LastSuccessfulUtc);
        Assert.Equal("NoCopies", changed.ResultCode);
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

    [Fact]
    public void FreshnessUsesSuccessfulCompletionAndExactDeadline()
    {
        var configured = monitor with { MaximumBackupAgeHours = 26 };
        var copy = Report();
        ExternalBackupTask Evaluate(DateTimeOffset now) => Assert.Single(ExternalBackupTask.Create([configured], [copy],
            [new(monitor.Id, monitor.Name, now, 1, 0)], agentId, now));
        Assert.False(Evaluate(Now.AddHours(26)).FreshnessWarning);
        var overdue = Evaluate(Now.AddHours(26).AddSeconds(1));
        Assert.True(overdue.FreshnessWarning);
        Assert.Equal("Нет свежей копии", overdue.ResultDescription);
        Assert.Equal(Now, overdue.LastSuccessfulUtc);
    }

    [Fact]
    public void EmptyHistoryWarnsOnlyAfterPollAndWhenEnabled()
    {
        var configured = monitor with { MaximumBackupAgeHours = 26 };
        var unpolled = Assert.Single(ExternalBackupTask.Create([configured], [], [], agentId, Now));
        Assert.False(unpolled.FreshnessWarning);
        var polled = unpolled with { Poll = new(monitor.Id, monitor.Name, Now, 0, 0) };
        Assert.True(polled.FreshnessWarning);
        Assert.False((polled with { Monitor = configured with { Enabled = false } }).FreshnessWarning);
        Assert.False((polled with { Monitor = monitor }).FreshnessWarning);
        var error = polled with { Poll = polled.Poll! with { Error = "Connection failed" } };
        Assert.False(error.FreshnessWarning);
        Assert.Equal("Ошибка опроса", error.ResultDescription);
    }

    [Fact]
    public void FailureAndOtherDatabaseDoNotRefreshSuccessfulCopy()
    {
        var configured = monitor with { MaximumBackupAgeHours = 1 };
        var failed = Report(minutes: 120) with { Observation = Report(minutes: 120).Observation with { Outcome = ExternalBackupOutcome.Failed } };
        var task = Assert.Single(ExternalBackupTask.Create([configured], [Report(), failed, Report("Other", 180)],
            [new(monitor.Id, monitor.Name, Now.AddHours(3), 3, 0)], agentId, Now.AddHours(3)));
        Assert.True(task.FreshnessWarning);
        Assert.Equal(Now, task.LastSuccessfulUtc);
        Assert.Equal(failed, task.Latest);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(87601)]
    public void RejectsInvalidFreshnessThreshold(int hours) =>
        Assert.Throws<InvalidDataException>(() => (monitor with { MaximumBackupAgeHours = hours }).Validate());

    [Fact]
    public void FreshnessRequiresSpecificDatabaseAndSufficientHistory()
    {
        Assert.Throws<InvalidDataException>(() => (monitor with { MaximumBackupAgeHours = 26, DatabaseName = "" }).Validate());
        Assert.Throws<InvalidDataException>(() => (monitor with { MaximumBackupAgeHours = 26, LookbackDays = 1 }).Validate());
        (monitor with { MaximumBackupAgeHours = 26 }).Validate();
    }

    [Fact]
    public void FreshnessSnapshotRejectsContradictoryWarningAndRoundTrips()
    {
        var source = new MonitoredSource(monitor.Id, monitor.Name, "SqlServerHistory", true, Now, false)
        { MaximumBackupAgeHours = 26, LastSuccessfulUtc = Now.AddHours(-27), FreshnessWarning = true };
        MonitoringSnapshot Snapshot(MonitoredSource value) => new(Now, "test", [], null, 0, [value]);
        Assert.True(MonitoringValidation.Valid(Snapshot(source)));
        Assert.False(MonitoringValidation.Valid(Snapshot(source with { ObservationStatus = "Invented" })));
        Assert.True(MonitoringValidation.Valid(Snapshot(source with { ObservationStatus = "Overdue" })));
        Assert.False(MonitoringValidation.Valid(Snapshot(source with { Failed = true })));
        Assert.False(MonitoringValidation.Valid(Snapshot(source with { Enabled = false })));
        Assert.False(MonitoringValidation.Valid(Snapshot(source with { MaximumBackupAgeHours = null })));
        Assert.False(MonitoringValidation.Valid(Snapshot(source with { PolledUtc = null })));
        Assert.Equal(source, JsonSerializer.Deserialize<MonitoredSource>(JsonSerializer.Serialize(source)));
        Assert.True(MonitoringValidation.Valid(Snapshot(new(monitor.Id, monitor.Name, "SqlServerHistory", true, Now, false))));
    }

    [Fact]
    public void FreshnessUsesSourceTimeZoneRatherThanObservationArrival()
    {
        var report = Report() with { ObservedUtc = Now.AddDays(1) };
        report = report with { Observation = report.Observation with
            { StartedAt = Now.AddHours(3).AddMinutes(-1).DateTime, FinishedAt = Now.AddHours(3).DateTime, TimeZoneId = "Europe/Moscow" } };
        var task = Assert.Single(ExternalBackupTask.Create([monitor with { MaximumBackupAgeHours = 1 }], [report],
            [new(monitor.Id, monitor.Name, Now.AddHours(2), 1, 0)], agentId, Now.AddHours(2)));
        Assert.Equal(Now, task.LastSuccessfulUtc);
        Assert.True(task.FreshnessWarning);
    }

    [Fact]
    public void SqlCopyTypesKeepIndependentSuccessfulTimes()
    {
        var full = Report(minutes: -120);
        var differential = Report(minutes: -60);
        differential = differential with { Observation = differential.Observation with { BackupType = "I" } };
        var log = Report(); log = log with { Observation = log.Observation with { BackupType = "L" } };
        var failed = Report(minutes: 10); failed = failed with { Observation = failed.Observation with { Outcome = ExternalBackupOutcome.Failed } };
        var task = Assert.Single(ExternalBackupTask.Create([monitor], [full, differential, log, failed, Report("Other", 20)], [], agentId, Now));
        Assert.Equal(Now.AddMinutes(-120), task.LastFullUtc);
        Assert.Equal(Now.AddMinutes(-60), task.LastDifferentialUtc);
        Assert.Equal(Now, task.LastLogUtc);
        Assert.Equal(Now, task.LastSuccessfulUtc);
        Assert.Contains("Дифференциальная", task.ResultDetails);
        var changed = Assert.Single(ExternalBackupTask.Create([monitor with { DatabaseName = "NewDatabase" }], [full, differential, log], [], agentId, Now));
        Assert.Null(changed.LastFullUtc); Assert.Null(changed.LastDifferentialUtc); Assert.Null(changed.LastLogUtc);
    }

    [Fact]
    public void TypeDescriptionsDoNotAlterImmutableObservationJson()
    {
        var observation = Report().Observation;
        Assert.Equal("Полная", observation.BackupTypeDescription);
        Assert.DoesNotContain("BackupTypeDescription", JsonSerializer.Serialize(observation));
        Assert.Equal("L", (observation with { BackupType = "L", Evidence = "PgRestoreCatalogChecked" }).BackupTypeDescription);
    }

    [Fact]
    public void SqlTypeSnapshotRejectsWrongKindAndNonUtcTimes()
    {
        var source = new MonitoredSource(monitor.Id, monitor.Name, "SqlServerHistory", true, Now, false)
        { SqlBackupTypesReported = true, LastFullUtc = Now };
        MonitoringSnapshot Snapshot(MonitoredSource value) => new(Now, "test", [], null, 0, [value]);
        Assert.True(MonitoringValidation.Valid(Snapshot(source)));
        Assert.False(MonitoringValidation.Valid(Snapshot(source with { Kind = "PostgreSqlReports" })));
        Assert.False(MonitoringValidation.Valid(Snapshot(source with { SqlBackupTypesReported = false })));
        Assert.False(MonitoringValidation.Valid(Snapshot(source with { LastFullUtc = Now.ToOffset(TimeSpan.FromHours(3)) })));
    }

    [Fact]
    public void SnapshotRejectsExceptionTextInsteadOfPollErrorCode()
    {
        var source = new MonitoredSource(monitor.Id, monitor.Name, "SqlServerHistory", true, Now, true)
        { PollErrorCode = "Login" };
        MonitoringSnapshot Snapshot(MonitoredSource value) => new(Now, "test", [], null, 0, [value]);
        Assert.True(MonitoringValidation.Valid(Snapshot(source)));
        Assert.False(MonitoringValidation.Valid(Snapshot(source with { Failed = false })));
        Assert.False(MonitoringValidation.Valid(Snapshot(source with { PollErrorCode = "Server=private;password=private" })));
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
