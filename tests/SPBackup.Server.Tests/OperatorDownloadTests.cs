using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Server.Tests;

public sealed class OperatorDownloadTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    [ServerFact]
    public async Task AgentTransfersARealRecoveryChainAndRevocationStopsAReadAlreadyInProgress()
    {
        var enrollment = await fixture.EnrollAsync();
        var directory = Directory.CreateDirectory(Path.Combine(fixture.Root, "download-agent")).FullName;
        var source = Directory.CreateDirectory(Path.Combine(directory, "input")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(directory, "copies")).FullName;
        var original = new byte[2 * 1024 * 1024 + 17];
        new Random(123).NextBytes(original);
        var firstFile = Path.Combine(source, "document.bin");
        await File.WriteAllBytesAsync(firstFile, original);
        var job = new BackupJob { Name = "File snapshot", Source = source, DestinationDirectory = destination,
            Kind = DatabaseKind.FilesAndFolders, FileBackupMode = FileBackupMode.Incremental, ScheduleKind = ScheduleKind.Manual, KeepLast = 8 };
        await new JobStore(Path.Combine(directory, "jobs.json"), useLocalTime: true).SaveAsync([job]);
        var history = new BackupRunHistory(Path.Combine(directory, "runs.json"));
        var catalog = new BackupCatalog(Path.Combine(directory, "copies.json"));
        var executor = new UnusedExecutor();
        var runner = new LocalBackupRunner(executor, new(new FileBackupStorage(), catalog), history, Path.Combine(directory, "work"));
        Assert.Equal(BackupRunStatus.Succeeded, (await runner.RunAsync(job)).Status);
        await File.WriteAllTextAsync(Path.Combine(source, "extra.txt"), "incremental version");
        var run = await runner.RunAsync(job);
        Assert.Equal(BackupRunStatus.Succeeded, run.Status);
        Assert.NotNull(run.Backup?.FileSnapshot?.ParentSnapshotId);
        var connection = new ServerMonitoringSettings(fixture.Address.ToString(), enrollment.AgentId, "test-vault", fixture.CertificateSha256)
            { AllowRecoveryKitExport = true };
        await ServerMonitoringDispatcher.SaveSettingsAsync(directory, connection);

        await fixture.Store.SaveMonitoringAsync(enrollment.AgentId, new(DateTimeOffset.UtcNow, "fixture",
            [new(job.Id, job.Name, "FilesAndFolders", true, "Manual", null, 1)], null, 0), default);
        var secret = Guid.NewGuid().ToString("N");
        var grant = $"{enrollment.AgentId:D}:{job.Id:D}";
        using var created = await fixture.Administrator.PostAsync("management/operators", OperatorForm("kit-reader", secret, grant, download: true));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        using var otherCreated = await fixture.Administrator.PostAsync("management/operators", OperatorForm("kit-monitor", secret, grant, download: false));
        Assert.Equal(HttpStatusCode.Redirect, otherCreated.StatusCode);
        using var reader = fixture.Client();
        using var observer = fixture.Client();
        await fixture.SignInAsync(reader, password: secret, name: "kit-reader");
        await fixture.SignInAsync(observer, password: secret, name: "kit-monitor");

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var agent = new LocalAgentHost(directory, executor, new TestCredential(enrollment.Token)).RunAsync(cancellationToken: stop.Token);
        try
        {
            var historyPath = $"operator/agents/{enrollment.AgentId:D}/jobs/{job.Id:D}?run={run.RunId:D}";
            var ready = await WaitAsync(reader, historyPath, "Подготовить комплект", stop.Token);
            Assert.DoesNotContain("Подготовить комплект", WebUtility.HtmlDecode(await observer.GetStringAsync(historyPath, stop.Token)));
            var fields = new Dictionary<string, string> { ["agent"] = enrollment.AgentId.ToString("D"),
                ["job"] = job.Id.ToString("D"), ["run"] = run.RunId.ToString("D"), ["destination"] = "0",
                ["__RequestVerificationToken"] = ServerFixture.AntiToken(ready) };
            var deniedForm = new Dictionary<string, string>(fields)
            {
                ["__RequestVerificationToken"] = ServerFixture.AntiToken(await observer.GetStringAsync("account", stop.Token))
            };
            using (var denied = await observer.PostAsync("management/recovery-downloads", new FormUrlEncodedContent(deniedForm), stop.Token))
                Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            var invalidPoint = new Dictionary<string, string>(fields) { ["destination"] = "999" };
            using (var denied = await reader.PostAsync("management/recovery-downloads", new FormUrlEncodedContent(invalidPoint), stop.Token))
                Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            using var begin = await reader.PostAsync("management/recovery-downloads", new FormUrlEncodedContent(fields), stop.Token);
            Assert.Equal(HttpStatusCode.Redirect, begin.StatusCode);
            var statusPath = begin.Headers.Location!.OriginalString.TrimStart('/');
            var id = Guid.Parse(statusPath.Split('/')[^1]);
            await WaitAsync(reader, statusPath, "Комплект готов", stop.Token);
            var filePath = $"management/recovery-downloads/{id:D}/file";
            using (var forbidden = await observer.GetAsync(filePath, stop.Token)) Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
            using (var range = new HttpRequestMessage(HttpMethod.Get, filePath))
            {
                range.Headers.Range = new RangeHeaderValue(0, 3);
                using var part = await reader.SendAsync(range, stop.Token);
                Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
                Assert.Equal(new byte[] { 80, 75, 3, 4 }, await part.Content.ReadAsByteArrayAsync(stop.Token));
            }
            using var bytes = new MemoryStream(await reader.GetByteArrayAsync(filePath, stop.Token));
            using var archive = new ZipArchive(bytes);
            var kit = Directory.CreateDirectory(Path.Combine(fixture.Root, "kit")).FullName;
            archive.ExtractToDirectory(kit);
            Assert.Equal(2, (await FileRecoveryKit.InspectAsync(kit, stop.Token)).Archives);
            var restored = await FileRecoveryKit.RestoreAsync(kit, Directory.CreateDirectory(Path.Combine(fixture.Root, "restored")).FullName,
                Path.Combine(fixture.Root, "work-clean-profile"), password: null, stop.Token);
            Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(restored.Directory, "document.bin"), stop.Token));
            Assert.Equal("incremental version", await File.ReadAllTextAsync(Path.Combine(restored.Directory, "extra.txt"), stop.Token));

            var account = Assert.Single(await fixture.Store.OperatorsAsync(stop.Token), value => value.Name == "kit-reader");
            var clock = new ManualClock();
            using (var expiring = new RecoveryDownloads(fixture.Store, clock))
            {
                await expiring.StartAsync(stop.Token);
                try
                {
                    var expiringId = await expiring.CreateAsync(account.Id, account.Stamp, enrollment.AgentId, job.Id, run.RunId, 0, stop.Token);
                    Assert.NotNull(await expiring.StatusAsync(account.Id, account.Stamp, expiringId, stop.Token));
                    clock.UtcNow = clock.UtcNow.AddMinutes(31);
                    Assert.Null(await expiring.StatusAsync(account.Id, account.Stamp, expiringId, stop.Token));
                    Assert.Empty(expiring.PendingForAdministration());
                }
                finally { await expiring.StopAsync(CancellationToken.None); }
            }
            var context = new DefaultHttpContext();
            using var body = new PausedBody();
            context.Response.Body = body;
            var transfer = fixture.App.Services.GetRequiredService<RecoveryDownloads>().SendAsync(account.Id, account.Stamp, id, context);
            try
            {
                await body.FirstBlock.Task.WaitAsync(stop.Token);
                using var revoked = await fixture.Administrator.PostAsync("management/operators",
                    new FormUrlEncodedContent(new Dictionary<string, string>
                    { ["id"] = account.Id.ToString("D"), ["stamp"] = account.Stamp.ToString("D"), ["enabled"] = "true", ["grant"] = grant }), stop.Token);
                Assert.Equal(HttpStatusCode.Redirect, revoked.StatusCode);
            }
            finally { body.Resume.TrySetResult(); }
            await Assert.ThrowsAsync<ServerRequestException>(() => transfer);
            Assert.Equal(1024L * 1024, body.Length);
            await fixture.SignInAsync(reader, password: secret, name: "kit-reader");
            using var resume = new HttpRequestMessage(HttpMethod.Get, filePath);
            resume.Headers.Range = new RangeHeaderValue(1024 * 1024, null);
            using var retry = await reader.SendAsync(resume, stop.Token);
            Assert.Equal(HttpStatusCode.NotFound, retry.StatusCode);
        }
        finally
        {
            await stop.CancelAsync();
            await agent;
        }
    }

    private static async Task<string> WaitAsync(HttpClient client, string path, string expected, CancellationToken token)
    {
        while (true)
        {
            using var response = await client.GetAsync(path, token);
            var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(token));
            if (response.IsSuccessStatusCode && html.Contains(expected, StringComparison.Ordinal)) return html;
            if (html.Contains("Подготовка не завершилась", StringComparison.Ordinal))
                throw new InvalidOperationException("The agent reported a recovery kit preparation failure.");
            await Task.Delay(250, token);
        }
    }

    private static FormUrlEncodedContent OperatorForm(string name, string password, string grant, bool download)
    {
        var values = new Dictionary<string, string> { ["name"] = name, ["password"] = password, ["enabled"] = "true", ["grant"] = grant };
        if (download) values["download"] = grant;
        return new(values);
    }

    private sealed class TestCredential(string token) : IFtpCredentialStore
    {
        public Task<NetworkCredential> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(new NetworkCredential("agent", token));
    }

    private sealed class UnusedExecutor : IBackupExecutor
    {
        public Task<string> CreateAsync(BackupJob job, string workDirectory, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("File jobs have their own executor.");
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class PausedBody : MemoryStream
    {
        public TaskCompletionSource FirstBlock { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            FirstBlock.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
        }
    }
}
