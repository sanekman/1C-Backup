using System.IO.Compression;
using System.Text.Json;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class DiagnosticExecutionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sp-diagnostic-execution-" + Guid.NewGuid().ToString("N"));
    private string Profile => Path.Combine(root, "profile");
    private const string Secret = "never-export-private-name-path-password";
    public DiagnosticExecutionTests() => Directory.CreateDirectory(Profile);
    private async Task Write(string name, object value)
    {
        var path = Path.Combine(Profile, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value));
    }
    private async Task<(JsonDocument Execution, JsonDocument Info)> Export(DiagnosticWindowSnapshot? window = null)
    {
        using var scope = DiagnosticLog.Use(new DiagnosticLog(Profile, DiagnosticComponent.Tests, "test", "test"));
        var target = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        await new DiagnosticBundle(Profile, "test", "test", window).ExportAsync(target);
        using var zip = ZipFile.OpenRead(target);
        foreach (var entry in zip.Entries)
        {
            using var text = new StreamReader(entry.Open());
            var content = await text.ReadToEndAsync();
            Assert.DoesNotContain(Secret, content);
            Assert.DoesNotContain(Profile, content);
        }
        using var reader = new StreamReader(zip.GetEntry("execution-summary.json")!.Open());
        using var info = new StreamReader(zip.GetEntry("support-info.json")!.Open());
        return (JsonDocument.Parse(await reader.ReadToEndAsync()), JsonDocument.Parse(await info.ReadToEndAsync()));
    }

    [Fact]
    public async Task SavedWindowPlanDistinguishesTriggersAndExportsNoConnectionOrJobNames()
    {
        var job = new BackupJob { Name = Secret, Source = Secret, DestinationDirectory = Secret,
            ScheduleKind = ScheduleKind.Daily, DailyAt = new TimeOnly(23, 0), TimeZoneId = "UTC" };
        var jobs = new[] { job, job with { Id = Guid.NewGuid(), Enabled = false },
            job with { Id = Guid.NewGuid(), ScheduleKind = ScheduleKind.Manual },
            job with { Id = Guid.NewGuid(), ScheduleKind = ScheduleKind.SystemStartup },
            job with { Id = Guid.NewGuid(), TimeZoneId = Secret } };
        var snapshot = DiagnosticWindowSnapshot.Capture(jobs, AgentMode.Managed, 17, true, false, job.Id);
        var (execution, info) = await Export(snapshot);
        using (execution) using (info)
        {
            var window = execution.RootElement.GetProperty("Window");
            Assert.Equal(17, window.GetProperty("ConfigurationRevision").GetInt64());
            Assert.False(window.GetProperty("WindowSchedulerRunning").GetBoolean());
            Assert.Equal("Managed", window.GetProperty("Mode").GetString());
            var rows = window.GetProperty("Jobs").EnumerateArray().ToArray();
            Assert.Equal(new[] { "Scheduled", "Disabled", "Manual", "SystemStartup", "InvalidSchedule" },
                rows.Select(r => r.GetProperty("Trigger").GetString()));
            Assert.Equal(BackupSchedule.NextRun(job, snapshot.CapturedUtc), rows[0].GetProperty("CalculatedNextRunUtc").GetDateTimeOffset());
            Assert.All(rows.Skip(1), r => Assert.Equal(JsonValueKind.Null, r.GetProperty("CalculatedNextRunUtc").ValueKind));
            Assert.Equal(JsonValueKind.Null, execution.RootElement.GetProperty("Agent").ValueKind);
        }
    }

    [Fact]
    public async Task StaleAgentAndFinishedProgressRemainSnapshotsAndPrivateTextNeverLeavesProfile()
    {
        var time = DateTimeOffset.UtcNow.AddHours(-2);
        var session = Guid.NewGuid(); var job = Guid.NewGuid(); var run = Guid.NewGuid();
        await Write("local-agent.json", new LocalAgentState(session, 42, time, time, LocalAgentPhase.Running,
            Error: Secret, MonitoringError: Secret) { RunsSchedules = true, Warning = Secret, CanRunManually = true });
        await Write("backup-progress.json", new BackupProgressSnapshot(run, job, Secret, 42, 5, time,
            new(BackupStage.Completed, 123, 123), []));
        var request = new LocalManualRunRequest(Guid.NewGuid(), Guid.NewGuid(), job, time);
        var relative = Path.Combine("manual-runs", "pending", request.RequestId.ToString("N") + ".json");
        await Write(relative, request);
        var before = Directory.GetFiles(Profile, "*.json", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllText);
        var (execution, info) = await Export();
        using (execution) using (info)
        {
            var agent = execution.RootElement.GetProperty("Agent");
            Assert.Equal(session, agent.GetProperty("SessionId").GetGuid());
            Assert.True(agent.GetProperty("HasWarning").GetBoolean());
            Assert.True(agent.GetProperty("HasError").GetBoolean());
            Assert.True(agent.GetProperty("SnapshotAgeMilliseconds").GetInt64() >= 7200000);
            Assert.True(execution.RootElement.GetProperty("LastProgress").GetProperty("Finished").GetBoolean());
            Assert.Equal(request.SessionId, execution.RootElement.GetProperty("PendingManualRequests")[0].GetProperty("SessionId").GetGuid());
        }
        Assert.All(before, pair => Assert.Equal(pair.Value, File.ReadAllText(pair.Key)));
    }

    [Fact]
    public async Task DamagedAgentAndOneBadCommandDoNotHideValidProgressAndOtherCommands()
    {
        await File.WriteAllTextAsync(Path.Combine(Profile, "local-agent.json"), Secret);
        var job = Guid.NewGuid();
        await Write("backup-progress.json", new BackupProgressSnapshot(Guid.NewGuid(), job, Secret, 42, 5, DateTimeOffset.UtcNow,
            new(BackupStage.CreatingBackup), []));
        var request = new LocalManualRunRequest(Guid.NewGuid(), Guid.NewGuid(), job, DateTimeOffset.UtcNow);
        await Write(Path.Combine("manual-runs", "pending", request.RequestId.ToString("N") + ".json"), request);
        await Write(Path.Combine("manual-runs", "pending", Guid.NewGuid().ToString("N") + ".json"), new { Error = Secret });
        var (execution, info) = await Export();
        using (execution) using (info)
        {
            Assert.Equal("Unavailable", execution.RootElement.GetProperty("AgentStatus").GetString());
            Assert.Equal("Available", execution.RootElement.GetProperty("ProgressStatus").GetString());
            Assert.Equal("Partial", execution.RootElement.GetProperty("PendingManualRequestsStatus").GetString());
            Assert.Single(execution.RootElement.GetProperty("PendingManualRequests").EnumerateArray());
            Assert.Equal(2, info.RootElement.GetProperty("Skipped").GetInt32());
        }
    }

    [Fact]
    public async Task MissingProfileStateIsNotCreatedAndIsNotReportedAsRunning()
    {
        var (execution, info) = await Export();
        using (execution) using (info)
        {
            Assert.Equal("Missing", execution.RootElement.GetProperty("AgentStatus").GetString());
            Assert.Equal("Missing", execution.RootElement.GetProperty("ProgressStatus").GetString());
            Assert.Equal("Missing", execution.RootElement.GetProperty("PendingManualRequestsStatus").GetString());
            Assert.Equal(JsonValueKind.Null, execution.RootElement.GetProperty("Window").ValueKind);
        }
        Assert.Empty(Directory.GetFiles(Profile, "*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task InvalidHistoryDoesNotPreventExportOfTheExecutionSnapshot()
    {
        await Write("runs.json", new[] { new BackupRun(Guid.Empty, Guid.NewGuid(), Secret, DateTimeOffset.UtcNow,
            null, BackupRunStatus.Running, Secret) });
        var (execution, info) = await Export();
        using (execution) using (info)
        {
            Assert.Equal("Unavailable", info.RootElement.GetProperty("HistoryStatus").GetString());
            Assert.Equal("Missing", execution.RootElement.GetProperty("AgentStatus").GetString());
            Assert.Equal(1, info.RootElement.GetProperty("Skipped").GetInt32());
        }
    }

    [Fact]
    public async Task OversizedHistoryDoesNotPreventExportOrReplaceTheOriginalHistory()
    {
        var path = Path.Combine(Profile, "runs.json");
        const long length = 32 * 1024 * 1024 + 1;
        await using (var file = File.Create(path)) file.SetLength(length);
        var (execution, info) = await Export();
        using (execution) using (info)
        {
            Assert.Equal("Unavailable", info.RootElement.GetProperty("HistoryStatus").GetString());
            Assert.Equal(1, info.RootElement.GetProperty("Skipped").GetInt32());
        }
        Assert.Equal(length, new FileInfo(path).Length);
    }

    [Fact]
    public async Task PendingCommandsAreBoundedAndTruncationIsExplicit()
    {
        var session = Guid.NewGuid(); var job = Guid.NewGuid();
        for (var index = 0; index < 1001; index++)
        {
            var request = new LocalManualRunRequest(Guid.NewGuid(), session, job, DateTimeOffset.UtcNow);
            await Write(Path.Combine("manual-runs", "pending", request.RequestId.ToString("N") + ".json"), request);
        }
        var (execution, info) = await Export();
        using (execution) using (info)
        {
            Assert.True(execution.RootElement.GetProperty("PendingManualRequestsTruncated").GetBoolean());
            Assert.Equal(1000, execution.RootElement.GetProperty("PendingManualRequests").GetArrayLength());
        }
        Assert.Equal(1001, Directory.GetFiles(Path.Combine(Profile, "manual-runs", "pending")).Length);
    }

    [Fact]
    public async Task OversizedAndLinkedSnapshotsAreOmittedWithoutCopyingTheirContents()
    {
        var path = Path.Combine(Profile, "local-agent.json");
        await using (var file = File.Create(path)) file.SetLength(1024 * 1024 + 1);
        var external = Path.Combine(root, "external.json");
        await File.WriteAllTextAsync(external, Secret);
        File.CreateSymbolicLink(Path.Combine(Profile, "backup-progress.json"), external);
        var (execution, info) = await Export();
        using (execution) using (info)
        {
            Assert.Equal("Unavailable", execution.RootElement.GetProperty("AgentStatus").GetString());
            Assert.Equal("Unavailable", execution.RootElement.GetProperty("ProgressStatus").GetString());
            Assert.Equal(2, info.RootElement.GetProperty("Skipped").GetInt32());
        }
        Assert.Equal(Secret, await File.ReadAllTextAsync(external));
    }
    public void Dispose() => Directory.Delete(root, true);
}
