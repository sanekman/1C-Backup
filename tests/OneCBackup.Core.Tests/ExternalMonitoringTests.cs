using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class ExternalMonitoringTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OneCBackup-monitor-" + Guid.NewGuid());
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
        { Converters = { new JsonStringEnumConverter() } };
    private readonly AgentSettings settings = new() { Mode = AgentMode.Managed,
        ServerAddress = "https://monitor.example.com/prefix/", CredentialKey = "fixture-reference" };
    private ExternalBackupMonitor Monitor => new() { Id = monitorId, Name = "PG", Kind = ExternalBackupSourceKind.PostgreSqlReports, ReportDirectory = root };
    private readonly Guid monitorId = Guid.NewGuid();
    private ExternalBackupOutbox Queue => new(Path.Combine(root, "outbox", "reports.json"));
    private static ExternalBackupObservation Observation => new("cluster:run-1", "cluster", "database", "LogicalFull",
        new DateTime(2026, 10, 10, 1, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 10, 1, 2, 0, DateTimeKind.Utc),
        "UTC", ExternalBackupOutcome.Completed, 123, ["/external/backup.dump"], "PgRestoreCatalogChecked");

    public ExternalMonitoringTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task RepeatedPollsAndRestartDoNotDuplicateReports()
    {
        Assert.Equal(1, await Queue.ObserveAsync(settings, Monitor, [Observation, Observation]));
        Assert.Equal(0, await Queue.ObserveAsync(settings, Monitor, [Observation]));
        var report = Assert.Single(await Queue.ReadAsync());
        Assert.Equal(settings.AgentId, report.AgentId);
        Assert.Equal(settings.NormalizedServerAddress, report.TargetServer);
        Assert.Null(report.DeliveredUtc);
    }

    [Fact]
    public async Task MutatedExistingReportRejectsBatchWithoutDiscardingPreviousHistory()
    {
        await Queue.ObserveAsync(settings, Monitor, [Observation]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Queue.ObserveAsync(settings, Monitor,
            [Observation with { SourceKey = "new" }, Observation with { SizeBytes = 456 }]));
        Assert.Equal(Observation.SourceKey, Assert.Single(await Queue.ReadAsync()).Observation.SourceKey);
    }

    [Fact]
    public async Task FailedSendRemainsPendingAndRetryUsesSameReportId()
    {
        var queue = Queue;
        await queue.ObserveAsync(settings, Monitor, [Observation]);
        var report = Assert.Single(await queue.ReadAsync());
        var api = new FakeApi { Fail = true };
        await Assert.ThrowsAsync<IOException>(() => queue.SendPendingAsync(settings, api));
        Assert.Null(Assert.Single(await Queue.ReadAsync()).DeliveredUtc);
        api.Fail = false;
        Assert.Equal(1, await queue.SendPendingAsync(settings, api));
        Assert.NotNull(Assert.Single(await Queue.ReadAsync()).DeliveredUtc);
        Assert.Equal(0, await queue.SendPendingAsync(settings, api));
        Assert.Equal(new[] { report.ReportId, report.ReportId }, api.Ids);
    }

    [Fact]
    public async Task StandaloneAndDifferentAgentOrServerDoNotSendExistingQueue()
    {
        var queue = Queue;
        await queue.ObserveAsync(settings, Monitor, [Observation]);
        var api = new FakeApi();
        Assert.Equal(0, await queue.SendPendingAsync(settings with { Mode = AgentMode.Standalone }, api));
        Assert.Equal(0, await queue.SendPendingAsync(settings with { AgentId = Guid.NewGuid() }, api));
        Assert.Equal(0, await queue.SendPendingAsync(settings with { ServerAddress = "https://other.example.com/" }, api));
        Assert.Empty(api.Ids);
    }

    [Fact]
    public async Task StandaloneObservationsRemainLocalAfterSwitchingToManaged()
    {
        await Queue.ObserveAsync(settings with { Mode = AgentMode.Standalone }, Monitor, [Observation]);
        Assert.Null(Assert.Single(await Queue.ReadAsync()).TargetServer);
        var api = new FakeApi();
        Assert.Equal(0, await Queue.SendPendingAsync(settings, api));
    }

    [Fact]
    public async Task CorruptQueueIsNotOverwritten()
    {
        var path = Path.Combine(root, "broken.json");
        await File.WriteAllTextAsync(path, "broken");
        await Assert.ThrowsAsync<JsonException>(() => new ExternalBackupOutbox(path).ObserveAsync(settings, Monitor, [Observation]));
        Assert.Equal("broken", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task PostgreSqlImportsCompletedAndFailedReportsButIgnoresUnpublishedFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "completed.json"), JsonSerializer.Serialize(Observation, WireOptions));
        await File.WriteAllTextAsync(Path.Combine(root, "failed.json"), JsonSerializer.Serialize(Observation with
        { SourceKey = "cluster:failed", Outcome = ExternalBackupOutcome.Failed, SizeBytes = null,
            Locations = [], Error = "Backup failed" }, WireOptions));
        await File.WriteAllTextAsync(Path.Combine(root, "partial.json.tmp"), "broken partial report");
        var rows = await new PostgreSqlBackupReportSource().ReadAsync(Monitor);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Outcome == ExternalBackupOutcome.Failed);
        Assert.Empty(await new PostgreSqlBackupReportSource().ReadAsync(Monitor with { DatabaseName = "another" }));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{broken")]
    public async Task InvalidPostgreSqlReportIsAnErrorInsteadOfSuccessfulEmptyPoll(string content)
    {
        await File.WriteAllTextAsync(Path.Combine(root, "invalid.json"), content);
        await Assert.ThrowsAnyAsync<Exception>(() => new PostgreSqlBackupReportSource().ReadAsync(Monitor));
    }

    [Fact]
    public async Task ApiSendsStableIdempotencyKeyAndBearerToken()
    {
        await Queue.ObserveAsync(settings, Monitor, [Observation]);
        var report = Assert.Single(await Queue.ReadAsync());
        using var client = new AgentApiClient(new Credentials(), new Handler(async request =>
        {
            Assert.Equal($"https://monitor.example.com/prefix/api/v1/agents/{settings.AgentId}/external-backups", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(report.ReportId.ToString("D"), Assert.Single(request.Headers.GetValues("Idempotency-Key")));
            Assert.Equal("fixture-token", request.Headers.Authorization!.Parameter);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(report.ReportId, json.RootElement.GetProperty("reportId").GetGuid());
            Assert.Equal("Completed", json.RootElement.GetProperty("observation").GetProperty("outcome").GetString());
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        Assert.Equal(1, await Queue.SendPendingAsync(settings, client));
    }

    [Fact]
    public async Task ApiFailureDoesNotAcknowledgeReport()
    {
        await Queue.ObserveAsync(settings, Monitor, [Observation]);
        using var client = new AgentApiClient(new Credentials(), new Handler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        await Assert.ThrowsAsync<HttpRequestException>(() => Queue.SendPendingAsync(settings, client));
        Assert.Null(Assert.Single(await Queue.ReadAsync()).DeliveredUtc);
    }

    private sealed class FakeApi : IExternalBackupReportApi
    {
        public bool Fail { get; set; }
        public List<Guid> Ids { get; } = [];
        public Task SendExternalBackupAsync(AgentSettings settings, ExternalBackupReport report, CancellationToken cancellationToken)
        {
            Ids.Add(report.ReportId);
            if (Fail) throw new IOException("Network unavailable");
            return Task.CompletedTask;
        }
    }
    private sealed class Credentials : IAgentCredentialProvider
    {
        public Task<string> GetBearerTokenAsync(string credentialKey, CancellationToken cancellationToken) => Task.FromResult("fixture-token");
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
    public void Dispose() => Directory.Delete(root, recursive: true);
}
