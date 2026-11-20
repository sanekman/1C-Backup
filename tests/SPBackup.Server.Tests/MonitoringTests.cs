using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using SPBackup.Core;
using Xunit;
using M = SPBackup.Server.Monitoring;

namespace SPBackup.Server.Tests;
public sealed class MonitoringTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture server;
    public MonitoringTests(ServerFixture fixture) => server=fixture;
    private static M.MonitoredRun Run(bool incomplete=false) => new(Guid.NewGuid(),Guid.NewGuid(),"Files",DateTimeOffset.UtcNow.AddMinutes(-2),DateTimeOffset.UtcNow,"Succeeded",incomplete ? 1 : 0,incomplete,"Incremental",[new(0,true,128,incomplete)]);
    [ServerFact]
    public async Task CurrentScopeAndCopyHistoryIgnoreObsoleteReports()
    {
        var agent = await server.EnrollAsync();
        var previous = Run() with { BackupScope = new string('a', 64) };
        var currentScope = new string('b', 64);
        await server.Store.SaveRunAsync(agent.AgentId, previous, default);
        var job = new M.MonitoredJob(previous.JobId, previous.JobName, "FilesAndFolders", true, "Daily", null, 1)
        {
            BackupScope = currentScope
        };
        await server.Store.SaveMonitoringAsync(agent.AgentId,
            new(DateTimeOffset.UtcNow, "test", [job], null, 0), default);
        Assert.Empty(await server.Store.LastSuccessfulRunsAsync(agent.AgentId, default));

        var current = previous with { RunId = Guid.NewGuid(), BackupScope = currentScope };
        await server.Store.SaveRunAsync(agent.AgentId, current, default);
        Assert.Equal(current.RunId, Assert.Single(await server.Store.LastSuccessfulRunsAsync(agent.AgentId, default)).RunId);

        var observation = Run() with { Evidence = "SqlServerMsdbHistory", CopyKind = null };
        for (var index = 1; index <= 50; index++)
        {
            await server.Store.SaveRunAsync(agent.AgentId, observation with
            {
                RunId = Guid.NewGuid(),
                FinishedUtc = previous.FinishedUtc.AddSeconds(index)
            }, default);
        }

        var copies = await server.Store.RunsAsync(agent.AgentId, 0, default, agentCopiesOnly: true);
        Assert.Contains(copies.Runs, run => run.RunId == current.RunId);
        Assert.All(copies.Runs, run => Assert.Null(run.Evidence));
        Assert.DoesNotContain((await server.Store.RunsAsync(agent.AgentId, 0, default)).Runs, run => run.RunId == current.RunId);
    }

    [ServerFact]
    public async Task ReportsAreDurableIdempotentAndScopedToAgent()
    {
        var agent=await server.EnrollAsync(); var other=await server.EnrollAsync();
        using var client=server.Client(); client.DefaultRequestHeaders.Authorization=new("Bearer",agent.Token);
        var run=Run(true);
        var uri=$"api/v1/agents/{agent.AgentId}/runs";
        using var first=await client.PostAsync(uri,ServerFixture.Json(run)); Assert.Equal(HttpStatusCode.NoContent,first.StatusCode);
        using var retry=await client.PostAsync(uri,ServerFixture.Json(run)); Assert.Equal(HttpStatusCode.NoContent,retry.StatusCode);
        using var conflict=await client.PostAsync(uri,ServerFixture.Json(run with { WarningCount=4 })); Assert.Equal(HttpStatusCode.Conflict,conflict.StatusCode);
        using var forbidden=await client.PostAsync($"api/v1/agents/{other.AgentId}/runs",ServerFixture.Json(Run())); Assert.Equal(HttpStatusCode.Forbidden,forbidden.StatusCode);
        await server.RestartAsync();
        var history=await server.Store.RunsAsync(agent.AgentId,0,default);
        Assert.Single(history.Runs); Assert.True(history.Runs[0].Incomplete); Assert.Equal("Incremental",history.Runs[0].CopyKind);
        Assert.Empty(await server.Store.LastSuccessfulRunsAsync(agent.AgentId,default));
    }
    [ServerFact]
    public async Task InvalidReportsAndUntrustedClientsAreRejected()
    {
        var agent=await server.EnrollAsync(); using var client=server.Client();
        var uri=$"api/v1/agents/{agent.AgentId}/runs";
        using var anonymous=await client.PostAsync(uri,ServerFixture.Json(Run())); Assert.Equal(HttpStatusCode.Unauthorized,anonymous.StatusCode);
        client.DefaultRequestHeaders.Authorization=new("Bearer",agent.Token);
        using var invalid=await client.PostAsync(uri,ServerFixture.Json(Run() with { Status="Running" })); Assert.Equal(HttpStatusCode.BadRequest,invalid.StatusCode);
        using var invalidTime=await client.PostAsync(uri,ServerFixture.Json(Run() with { FinishedUtc=DateTimeOffset.MinValue })); Assert.Equal(HttpStatusCode.BadRequest,invalidTime.StatusCode);
        var management=await client.GetAsync($"api/v1/management/agents/{agent.AgentId}/runs"); Assert.Equal(HttpStatusCode.Unauthorized,management.StatusCode);
    }
    [ServerFact]
    public async Task SnapshotAndDashboardDistinguishPartialCopiesAndConnection()
    {
        var agent=await server.EnrollAsync(); var run=Run(true);
        await server.Store.SaveRunAsync(agent.AgentId,run,default);
        await server.Store.SaveMonitoringAsync(agent.AgentId,new(DateTimeOffset.UtcNow.AddYears(1),"test-agent",[new(run.JobId,"Files","FilesAndFolders",true,"Daily",DateTimeOffset.UtcNow.AddHours(1),1)],null,2),default);
        var overview=await server.Store.OverviewAsync(180,default); Assert.True(overview.Incomplete>=1); Assert.True(overview.LegacyPending>=2);
        using var page=await server.Administrator.GetAsync($"agents/{agent.AgentId}"); page.EnsureSuccessStatusCode();
        var html=await page.Content.ReadAsStringAsync(); Assert.Contains("Incremental",JsonSerializer.Serialize(run));
        Assert.Contains("Неполная копия",WebUtility.HtmlDecode(html)); Assert.Contains("Инкрементная",WebUtility.HtmlDecode(html));
        Assert.Contains("Ожидает передачи по данным агента: 2", WebUtility.HtmlDecode(html));
        Assert.Equal("30",page.Headers.GetValues("Refresh").Single());
        var monitoring=await server.Store.MonitoringAsync(agent.AgentId,default); Assert.True(monitoring!.ReceivedUtc < DateTimeOffset.UtcNow.AddMinutes(1));
    }
    [ServerFact]
    public async Task RealClientSendsHistoryAfterFailureWithoutChangingLocalJobs()
    {
        var agent=await server.EnrollAsync(); var root=Path.Combine(server.Root,Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var job=new BackupJob { Name="Files",Kind=DatabaseKind.FilesAndFolders,Source=root,DestinationDirectory=Path.Combine(root,"copies"),ScheduleKind=ScheduleKind.Manual };
        await new JobStore(Path.Combine(root,"jobs.json"),true).SaveAsync([job]);
        var before=await File.ReadAllBytesAsync(Path.Combine(root,"jobs.json"));
        var run=new BackupRun(Guid.NewGuid(),job.Id,job.Name,DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow,BackupRunStatus.Failed,"private-path",Error:"password=must-not-be-sent")
        { Issues = [new("AccessDenied", "Error", "Storage", 0, 5)] };
        await new BackupRunHistory(Path.Combine(root,"runs.json")).SaveAsync(run);
        new BackupProgressStore(root).Write(new(run.RunId,job.Id,job.Name,Environment.ProcessId,1,DateTimeOffset.UtcNow,new(BackupStage.Archiving),[]));
        using var cert=X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(server.Root,"certificate.pfx"),File.ReadAllText(Path.Combine(server.Root,"certificate-password.txt")));
        var settings=new ServerMonitoringSettings(server.Address.ToString(),agent.AgentId,"monitor",Convert.ToHexString(SHA256.HashData(cert.RawData)));
        await ServerMonitoringDispatcher.SaveSettingsAsync(root,settings);
        var secrets=new Credentials { Value="invalid-token" };
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var loop = new ServerMonitoringDispatcher(root,secrets).RunAsync(error =>
        {
            if (error is not null) failed.TrySetResult(error);
            else if (failed.Task.IsCompleted) recovered.TrySetResult();
        },stop.Token,TimeSpan.FromMilliseconds(25));
        try
        {
            Assert.IsType<HttpRequestException>(await failed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(File.Exists(Path.Combine(root,"server-monitoring-receipts.json")));
            Assert.False(loop.IsCompleted);
            secrets.Value=agent.Token;
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>loop); }
        await new ServerMonitoringDispatcher(root,secrets).PollAsync();
        Assert.Equal(before,await File.ReadAllBytesAsync(Path.Combine(root,"jobs.json")));
        var reports=await server.Store.RunsAsync(agent.AgentId,0,default); Assert.Single(reports.Runs);
        Assert.Equal("AccessDenied", Assert.Single(reports.Runs[0].Issues!).Code);
        var json=JsonSerializer.Serialize(reports); Assert.DoesNotContain("must-not-be-sent",json); Assert.DoesNotContain("private-path",json);
        Assert.Equal(0,(await server.Store.MonitoringAsync(agent.AgentId,default))!.Snapshot.PendingRuns);
        Assert.Null((await server.Store.MonitoringAsync(agent.AgentId,default))!.Snapshot.Progress);
        var active = new BackupRun(Guid.NewGuid(),job.Id,job.Name,DateTimeOffset.UtcNow,null,BackupRunStatus.Running,"private-path");
        await new BackupRunHistory(Path.Combine(root,"runs.json")).SaveAsync(active);
        new BackupProgressStore(root).Write(new(active.RunId,job.Id,job.Name,Environment.ProcessId,2,DateTimeOffset.UtcNow,new(BackupStage.RetryingStorage),[]));
        await new ServerMonitoringDispatcher(root,secrets).PollAsync();
        var retrying = (await server.Store.MonitoringAsync(agent.AgentId,default))!.Snapshot.Progress;
        Assert.NotNull(retrying); Assert.Equal(active.RunId,retrying.RunId);
        Assert.Equal("ConnectingStorage",retrying.Stage); Assert.Null(retrying.Percent);
    }
    [ServerFact]
    public async Task RejectedAndUnpreparedReportsStayOutOfTheReadyQueueAfterRestart()
    {
        var agent = await server.EnrollAsync();
        var root = Directory.CreateDirectory(Path.Combine(server.Root, Guid.NewGuid().ToString("N"))).FullName;
        var job = new BackupJob { Name = "Files", Kind = DatabaseKind.FilesAndFolders, Source = root,
            DestinationDirectory = Path.Combine(root, "copies"), ScheduleKind = ScheduleKind.Manual };
        await new JobStore(Path.Combine(root, "jobs.json"), true).SaveAsync([job]);
        var end = DateTimeOffset.UtcNow;
        var history = new BackupRunHistory(Path.Combine(root, "runs.json"));
        var rejected = new BackupRun(Guid.NewGuid(), job.Id, job.Name, end.AddMinutes(-1), end,
            BackupRunStatus.Succeeded, root);
        var accepted = rejected with { RunId = Guid.NewGuid() };
        var unprepared = rejected with { RunId = Guid.NewGuid(), DurationMilliseconds = -1 };
        await history.SaveAsync(rejected);
        await history.SaveAsync(accepted);
        await history.SaveAsync(unprepared);
        await server.Store.SaveRunAsync(agent.AgentId, new(rejected.RunId, job.Id, job.Name,
            rejected.StartedUtc, end, "Failed", 0, false, null, []), default);

        using var cert = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(server.Root, "certificate.pfx"),
            File.ReadAllText(Path.Combine(server.Root, "certificate-password.txt")));
        var settings = new ServerMonitoringSettings(server.Address.ToString(), agent.AgentId, "monitor", Convert.ToHexString(SHA256.HashData(cert.RawData)));
        await ServerMonitoringDispatcher.SaveSettingsAsync(root, settings);
        var secrets = new Credentials { Value = agent.Token };
        await Assert.ThrowsAsync<InvalidDataException>(() => new ServerMonitoringDispatcher(root, secrets).PollAsync());
        var delivery = await ServerMonitoringDispatcher.ReadDeliveryAsync(root, settings);
        Assert.Contains(accepted.RunId, delivery!.Value.Sent);
        Assert.Contains(rejected.RunId, delivery.Value.Rejected);

        await Assert.ThrowsAsync<InvalidDataException>(() => new ServerMonitoringDispatcher(root, secrets).PollAsync());
        var snapshot = (await server.Store.MonitoringAsync(agent.AgentId, default))!.Snapshot;
        Assert.Equal(1, snapshot.PendingRuns); // Keep the total available to older servers.
        Assert.Equal(0, snapshot.ReadyRuns);
        Assert.Equal(1, snapshot.CollectionWarnings);
        Assert.Equal(new M.MonitoredReportRejections(0, 1, 0), snapshot.ReportRejections);
        var overview = await server.Store.OverviewAsync(180, default);
        Assert.Equal(1, overview.Rejected);
        Assert.Equal(1, overview.Unprepared);
        using var page = await server.Administrator.GetAsync($"agents/{agent.AgentId}");
        page.EnsureSuccessStatusCode();
        var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
        Assert.Contains("Готово к отправке: 0", html);
        Assert.Contains("Сервер уже хранит другой отчёт с тем же ID запуска", html);
        Assert.DoesNotContain(root, html);

        var receiptPath = Path.Combine(root, "server-monitoring-receipts.json");
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(receiptPath))!.AsObject();
        Assert.True(legacy.Remove("RejectionCodes"));
        await File.WriteAllTextAsync(receiptPath, legacy.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => new ServerMonitoringDispatcher(root, secrets).PollAsync());
        var legacySnapshot = (await server.Store.MonitoringAsync(agent.AgentId, default))!.Snapshot;
        Assert.Equal(new M.MonitoredReportRejections(0, 0, 1), legacySnapshot.ReportRejections);
        Assert.False(M.MonitoringValidation.Valid(snapshot with { PendingRuns = 0 }));
        Assert.False(M.MonitoringValidation.Valid(snapshot with { ReportRejections = new(-1, 1, 0) }));
    }

    [ServerFact]
    public async Task ClockRollbackKeepsTheRunAndBothCopiesThroughReportRetry()
    {
        var agent = await server.EnrollAsync();
        var root = Path.Combine(server.Root, Guid.NewGuid().ToString("N"));
        var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        await File.WriteAllTextAsync(Path.Combine(source, "fixture.txt"), "Backup after a wall-clock correction");
        var job = new BackupJob
        {
            Name = "Files", Kind = DatabaseKind.FilesAndFolders, Source = source, ScheduleKind = ScheduleKind.Manual,
            DestinationDirectory = Path.Combine(root, "copies"), AdditionalDestinations = [new() { DestinationDirectory = Path.Combine(root, "second") }]
        };
        await new JobStore(Path.Combine(root, "jobs.json"), true).SaveAsync([job]);
        var history = new BackupRunHistory(Path.Combine(root, "runs.json"));
        var started = DateTimeOffset.UtcNow;
        var finished = started.AddHours(-1);
        var clock = new BackupWallClock(started);
        var runner = new LocalBackupRunner(new UnusedBackupExecutor(),
            new BackupCompletion(new FileBackupStorage(), new BackupCatalog(Path.Combine(root, "copies.json"))),
            history, Path.Combine(root, "work"), progress: item =>
            {
                if (item.Progress.Stage == BackupStage.CreatingBackup) clock.UtcNow = finished;
            }, timeProvider: clock);
        var run = await runner.RunAsync(job);
        Assert.Equal(BackupRunStatus.Succeeded, run.Status);
        Assert.Equal(started, run.StartedUtc);
        Assert.Equal(finished, run.FinishedUtc);
        Assert.True(run.DurationMilliseconds is >= 0);
        Assert.Equal(run.DurationMilliseconds, Assert.Single(await history.ReadAsync()).DurationMilliseconds);

        using var cert = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(server.Root, "certificate.pfx"),
            File.ReadAllText(Path.Combine(server.Root, "certificate-password.txt")));
        var settings = new ServerMonitoringSettings(server.Address.ToString(), agent.AgentId, "monitor", Convert.ToHexString(SHA256.HashData(cert.RawData)));
        await ServerMonitoringDispatcher.SaveSettingsAsync(root, settings);
        var secrets = new Credentials { Value = "invalid-token" };
        var dispatcher = new ServerMonitoringDispatcher(root, secrets);
        var unavailable = await Assert.ThrowsAsync<HttpRequestException>(() => dispatcher.PollAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, unavailable.StatusCode);
        Assert.Empty((await server.Store.RunsAsync(agent.AgentId, 0, default)).Runs);

        var report = ServerMonitoringDispatcher.ToReport(run);
        using var client = new ServerMonitoringClient(settings);
        // The same report may already exist without optional timing after an older server handled it.
        await client.SendRunAsync(settings, agent.Token, report with { Timing = null });
        secrets.Value = agent.Token;
        await dispatcher.PollAsync();
        await dispatcher.PollAsync();
        await client.SendRunAsync(settings, agent.Token, report with { Timing = null });

        var saved = Assert.Single((await server.Store.RunsAsync(agent.AgentId, 0, default)).Runs);
        Assert.Equal(run.RunId, saved.RunId);
        Assert.Equal(started, saved.FinishedUtc);
        Assert.Equal(finished, saved.DisplayFinishedUtc);
        Assert.Equal(run.DurationMilliseconds, saved.Timing!.DurationMilliseconds);
        Assert.Equal(2, saved.Destinations.Count);
        Assert.All(saved.Destinations, destination => Assert.True(destination.Succeeded));
        var monitoring = (await server.Store.MonitoringAsync(agent.AgentId, default))!.Snapshot;
        Assert.Equal(0, monitoring.PendingRuns);
        Assert.Equal(0, monitoring.CollectionWarnings);
        using var page = await server.Administrator.GetAsync($"agents/{agent.AgentId}?runId={run.RunId}");
        page.EnsureSuccessStatusCode();
        Assert.Contains("часы агента были переведены назад", WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync()));
    }

    private sealed class BackupWallClock(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class UnusedBackupExecutor : IBackupExecutor
    {
        public Task<string> CreateAsync(BackupJob job, string work, CancellationToken token) => throw new InvalidOperationException();
    }

    [ServerFact]
    public async Task PinnedTlsAcceptsOnlyTheSpecifiedServerCertificate()
    {
        var id=Guid.NewGuid(); var invitation=await server.InviteAsync(id);
        var settings=new ServerMonitoringSettings(server.Address.ToString(),id,"test",new string('0',64));
        using var wrong=new ServerMonitoringClient(settings);
        await Assert.ThrowsAsync<HttpRequestException>(()=>wrong.EnrollAsync(id,invitation.Code,"Pinned agent"));
        using var cert=X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(server.Root,"certificate.pfx"),File.ReadAllText(Path.Combine(server.Root,"certificate-password.txt")));
        using var correct=new ServerMonitoringClient(settings with { CertificateSha256=Convert.ToHexString(SHA256.HashData(cert.RawData)) });
        Assert.Equal(43,(await correct.EnrollAsync(id,invitation.Code,"Pinned agent")).Length);
    }
    [ServerFact]
    public async Task ExternalSqlHistoryIsSentWithSafeFieldsAndStableReportId()
    {
        var agent=await server.EnrollAsync();
        var root=Path.Combine(server.Root,Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var monitor=new ExternalBackupMonitor { Name="External PostgreSQL",Kind=ExternalBackupSourceKind.PostgreSqlReports,ReportDirectory=root };
        await new ExternalBackupMonitorStore(Path.Combine(root,"external-monitors.json")).SaveAsync([monitor]);
        var outbox=new ExternalBackupOutbox(Path.Combine(root,"external-backups.json"));
        var observation=new ExternalBackupObservation("stable-key","private-instance","private-database","Full",DateTime.UtcNow.AddMinutes(-2),DateTime.UtcNow.AddMinutes(-1),"UTC",ExternalBackupOutcome.Completed,4096,["private-location"],"PgRestoreCatalogChecked");
        await outbox.ObserveAsync(new AgentSettings { AgentId=agent.AgentId },monitor,[observation]);
        using var cert=X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(server.Root,"certificate.pfx"),File.ReadAllText(Path.Combine(server.Root,"certificate-password.txt")));
        await ServerMonitoringDispatcher.SaveSettingsAsync(root,new(server.Address.ToString(),agent.AgentId,"monitor",Convert.ToHexString(SHA256.HashData(cert.RawData))));
        await new ServerMonitoringDispatcher(root,new Credentials { Value=agent.Token }).PollAsync();
        await new ServerMonitoringDispatcher(root,new Credentials { Value=agent.Token }).PollAsync();
        var runs=await server.Store.RunsAsync(agent.AgentId,0,default);
        var run=Assert.Single(runs.Runs); Assert.Equal(monitor.Id,run.JobId); Assert.Equal(4096,run.Destinations[0].Bytes);
        Assert.Equal((await outbox.ReadAsync()).Single().ReportId,run.RunId);
        Assert.DoesNotContain("private-",JsonSerializer.Serialize(runs));
        var snapshot = (await server.Store.MonitoringAsync(agent.AgentId,default))!.Snapshot;
        Assert.Single(snapshot.Sources!);
        var task = Assert.Single(snapshot.Jobs);
        Assert.Equal(monitor.Id, task.JobId); Assert.Equal(monitor.Name, task.Name);
        Assert.Equal("Observation", task.TaskKind); Assert.Equal(0, task.DestinationCount);
        Assert.Equal("PostgreSqlReports", task.SourceKind); Assert.Null(task.NextRunUtc);
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(snapshot));
        using var page = await server.Administrator.GetAsync($"agents/{agent.AgentId}");
        page.EnsureSuccessStatusCode();
        var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
        Assert.Contains("Наблюдение · Отчёты PostgreSQL", html);
        Assert.DoesNotContain("хранилищ: 0", html);
        Assert.DoesNotContain("Хранилище 1:", html);
        Assert.Contains("Размер по внешнему отчёту: 4096 байт", html);
    }
    [ServerFact]
    public async Task RevokedAgentCanReconnectOnlyWithExplicitlyBoundInvitation()
    {
        var old=await server.EnrollAsync();
        using var revoke=await server.Administrator.PostAsync($"api/v1/management/agents/{old.AgentId}/revoke",null); revoke.EnsureSuccessStatusCode();
        using var client=server.Client();
        Assert.True((await server.Store.AgentAsync(old.AgentId,default))!.Revoked);
        var generic=await server.InviteAsync();
        using var rejected=await client.PostAsync("api/v1/enrollment",ServerFixture.Json(new EnrollmentRequest(generic.Code,old.AgentId,"Again")));
        Assert.Equal(HttpStatusCode.Conflict,rejected.StatusCode);
        Assert.True((await server.Store.AgentAsync(old.AgentId,default))!.Revoked);
        var bound=await server.InviteAsync(old.AgentId);
        using var allowed=await client.PostAsync("api/v1/enrollment",ServerFixture.Json(new EnrollmentRequest(bound.Code,old.AgentId,"Again")));
        Assert.True(allowed.StatusCode==HttpStatusCode.Created,await allowed.Content.ReadAsStringAsync());
        var replacement=JsonSerializer.Deserialize<EnrollmentResponse>(await allowed.Content.ReadAsStringAsync(),new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.NotEqual(old.Token,replacement.Token);
        Assert.Null(await server.Store.AuthenticateAgentAsync(old.Token,default));
        Assert.Equal(old.AgentId,await server.Store.AuthenticateAgentAsync(replacement.Token,default));
    }
    private sealed class Credentials : IFtpCredentialStore
    {
        public string Value="";
        public Task<NetworkCredential> GetAsync(string key,CancellationToken cancellationToken=default)=>Task.FromResult(new NetworkCredential("agent",Value));
    }
}
