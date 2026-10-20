using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using OneCBackup.Core;
using Xunit;
using M = OneCBackup.Server.Monitoring;

namespace OneCBackup.Server.Tests;
public sealed class MonitoringTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture server;
    public MonitoringTests(ServerFixture fixture) => server=fixture;
    private static M.MonitoredRun Run(bool incomplete=false) => new(Guid.NewGuid(),Guid.NewGuid(),"Files",DateTimeOffset.UtcNow.AddMinutes(-2),DateTimeOffset.UtcNow,"Succeeded",incomplete ? 1 : 0,incomplete,"Incremental",[new(0,true,128,incomplete)]);
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
        var overview=await server.Store.OverviewAsync(180,default); Assert.True(overview.Incomplete>=1); Assert.True(overview.Pending>=2);
        using var page=await server.Administrator.GetAsync($"agents/{agent.AgentId}"); page.EnsureSuccessStatusCode();
        var html=await page.Content.ReadAsStringAsync(); Assert.Contains("Incremental",JsonSerializer.Serialize(run));
        Assert.Contains("Неполная копия",WebUtility.HtmlDecode(html)); Assert.Contains("Инкрементная",WebUtility.HtmlDecode(html));
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
        var run=new BackupRun(Guid.NewGuid(),job.Id,job.Name,DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow,BackupRunStatus.Failed,"private-path",Error:"password=must-not-be-sent");
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
        var json=JsonSerializer.Serialize(reports); Assert.DoesNotContain("must-not-be-sent",json); Assert.DoesNotContain("private-path",json);
        Assert.Equal(0,(await server.Store.MonitoringAsync(agent.AgentId,default))!.Snapshot.PendingRuns);
        Assert.Null((await server.Store.MonitoringAsync(agent.AgentId,default))!.Snapshot.Progress);
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
        Assert.Single((await server.Store.MonitoringAsync(agent.AgentId,default))!.Snapshot.Sources!);
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
