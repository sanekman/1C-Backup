using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class ExternalPollingTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OneCBackup-poll-" + Guid.NewGuid());
    private readonly AgentSettings settings = new();
    private readonly DateTimeOffset now = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
    private ExternalBackupMonitorStore Store => new(Path.Combine(directory, "monitors.json"));
    private ExternalBackupOutbox Outbox => new(Path.Combine(directory, "outbox.json"));
    private ExternalBackupPollingStatusStore Status => new(directory);
    private ExternalBackupMonitor Monitor => new() { Name = "PG", Kind = ExternalBackupSourceKind.PostgreSqlReports,
        ReportDirectory = Path.Combine(directory, "reports"), PollIntervalSeconds = 60 };
    private static ExternalBackupObservation Observation => new("backup-1", "pg-host", "database", "LogicalFull",
        new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 10, 0, 1, 0, DateTimeKind.Utc),
        "UTC", ExternalBackupOutcome.Completed, 20, ["/backups/database.dump"], "PgRestoreCatalogChecked");
    public ExternalPollingTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task HonorsPerSourceIntervalAndPersistsDeduplicatedObservations()
    {
        var monitor = Monitor;
        await Store.SaveAsync([monitor]);
        var source = new Source();
        var poller = new ExternalBackupPoller(Store, Outbox, settings, Status, _ => source);
        Assert.Equal(1, Assert.Single(await poller.PollAsync(now)).Added);
        Assert.Empty(await poller.PollAsync(now.AddSeconds(59)));
        Assert.Equal(0, Assert.Single(await poller.PollAsync(now.AddSeconds(60))).Added);
        Assert.Equal(2, source.Calls);
        Assert.Single(await Outbox.ReadAsync());
        Assert.Null(Assert.Single(await Outbox.ReadAsync()).TargetServer);
        Assert.Equal(now.AddSeconds(60), Assert.Single(await Status.ReadAsync()).PolledUtc);
    }

    [Fact]
    public async Task FailedSourceDoesNotPreventHealthySourceAndDoesNotRetryEarly()
    {
        var failed = Monitor with { Name = "Broken" };
        var healthy = Monitor;
        await Store.SaveAsync([failed, healthy]);
        var source = new Source();
        var poller = new ExternalBackupPoller(Store, Outbox, settings, Status,
            monitor => monitor.Id == failed.Id ? new Source { Fail = true } : source);
        var results = await poller.PollAsync(now);
        Assert.Equal("Source unavailable", results[0].Error);
        Assert.Null(results[1].Error);
        Assert.Single(await Outbox.ReadAsync());
        Assert.Empty(await poller.PollAsync(now.AddSeconds(10)));
        Assert.Equal(2, (await Status.ReadAsync()).Count);
    }

    [Fact]
    public async Task DisabledSourcesAreSkippedAndConfigChangesApplyWithoutRestart()
    {
        var monitor = Monitor with { Enabled = false };
        await Store.SaveAsync([monitor]);
        var source = new Source();
        var poller = new ExternalBackupPoller(Store, Outbox, settings, Status, _ => source);
        Assert.Empty(await poller.PollAsync(now));
        Assert.Equal(0, source.Calls);
        await Store.SaveAsync([monitor with { Enabled = true }]);
        Assert.Single(await poller.PollAsync(now.AddSeconds(1)));
        await Store.SaveAsync([monitor with { Enabled = true, PollIntervalSeconds = 30 }]);
        Assert.Single(await poller.PollAsync(now.AddSeconds(2)));
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task NewPollerAfterRestartKeepsPermanentDeduplication()
    {
        await Store.SaveAsync([Monitor]);
        Assert.Equal(1, Assert.Single(await new ExternalBackupPoller(Store, Outbox, settings, Status, _ => new Source()).PollAsync(now)).Added);
        Assert.Equal(0, Assert.Single(await new ExternalBackupPoller(Store, Outbox, settings, Status, _ => new Source()).PollAsync(now.AddSeconds(5))).Added);
        Assert.Single(await Outbox.ReadAsync());
    }

    private sealed class Source : IExternalBackupSource
    {
        public bool Fail { get; init; }
        public int Calls { get; private set; }
        public Task<IReadOnlyList<ExternalBackupObservation>> ReadAsync(ExternalBackupMonitor monitor, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Fail) throw new IOException("Source unavailable");
            return Task.FromResult<IReadOnlyList<ExternalBackupObservation>>([Observation]);
        }
    }
    public void Dispose() => Directory.Delete(directory, true);
}
