using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SPBackup.Core;
using SPBackup.Core.Monitoring;
using ICSharpCode.SharpZipLib.Zip;

internal static partial class Program
{
    private sealed class ManagedServer : IAsyncDisposable
    {
        private readonly X509Certificate2 certificate;
        private WebApplication? app;
        public readonly string Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        public readonly Guid AgentId = Guid.NewGuid();
        private readonly Guid offer = Guid.NewGuid();
        public volatile bool Online;
        public volatile bool Accepted;
        public int Gets;
        public long ReportedRevision;
        public string Address => app!.Urls.Single().TrimEnd('/') + "/";
        public string Pin => Convert.ToHexString(SHA256.HashData(certificate.RawData));
        public BackupJob Job = null!;
        public ManagedServer()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var issued = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            var pfx = issued.Export(X509ContentType.Pfx);
            try { certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.DefaultKeySet); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }
        public async Task Start()
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
            app = builder.Build();
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
            app.Run(async context =>
            {
                if (context.Request.Headers.Authorization != "Bearer " + Token
                    || !context.Request.Path.StartsWithSegments($"/api/v1/agents/{AgentId:D}"))
                { context.Response.StatusCode = 401; return; }
                var configuration = context.Request.Path.Value!.EndsWith("/configuration", StringComparison.Ordinal);
                if (configuration) Interlocked.Increment(ref Gets);
                if (!Online) { context.Response.StatusCode = 503; return; }
                if (configuration)
                {
                    if (!AgentCapabilities.Parse(context.Request.Headers[AgentCapabilities.Header]).Contains(AgentCapabilities.ManagedExecutionV1))
                    { context.Response.StatusCode = 409; return; }
                    context.Response.Headers.ETag = "\"1\"";
                    context.Response.Headers[ConfigurationReceipt.OfferHeader] = offer.ToString("D");
                    if (context.Request.Headers.IfNoneMatch == "\"1\"") { context.Response.StatusCode = 304; return; }
                    await context.Response.WriteAsJsonAsync(new ServerConfiguration(AgentId, 1, [Job]), json, context.RequestAborted);
                }
                else if (context.Request.Path.Value.EndsWith("/configuration-receipt", StringComparison.Ordinal))
                {
                    var receipt = await context.Request.ReadFromJsonAsync<ConfigurationReceipt>(json, context.RequestAborted);
                    if (receipt?.AgentId != AgentId || receipt.OfferId != offer || receipt.Revision != 1 || receipt.Status != "Accepted")
                    { context.Response.StatusCode = 400; return; }
                    Accepted = true; context.Response.StatusCode = 204;
                }
                else if (context.Request.Path.Value.EndsWith("/runs", StringComparison.Ordinal))
                {
                    var run = await context.Request.ReadFromJsonAsync<MonitoredRun>(json, context.RequestAborted);
                    if (!MonitoringValidation.Valid(run)) { context.Response.StatusCode = 400; return; }
                    if (run!.ConfigurationRevision is { } revision) Interlocked.Exchange(ref ReportedRevision, revision);
                    context.Response.StatusCode = 204;
                }
                else context.Response.StatusCode = 204;
            });
            await app.StartAsync();
        }
        public async ValueTask DisposeAsync()
        { try { if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); } } finally { certificate.Dispose(); } }
    }

    private static async Task VerifyManagedService(string executable, string directory, List<string> checks)
    {
        var originalSettings = await File.ReadAllBytesAsync(Path.Combine(directory, "agent.json"));
        var jobs = await File.ReadAllBytesAsync(Path.Combine(directory, "jobs.json"));
        var monitorPath = Path.Combine(directory, "server-monitoring.json");
        var originalMonitoring = File.Exists(monitorPath) ? await File.ReadAllBytesAsync(monitorPath) : null;
        var credentials = new WindowsFtpCredentialStore();
        var key = "managed-smoke-" + Guid.NewGuid().ToString("N");
        await using var server = new ManagedServer();
        await server.Start();
        var source = Path.Combine(directory, "managed-source"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "1Cv8.1CD"), "managed service data");
        var localJob = (await new JobStore(Path.Combine(directory, "jobs.json")).LoadAsync()).First();
        server.Job = new() { Id = localJob.Id, Name = "Managed service job", Kind = DatabaseKind.OneCFile,
            OneCMethod = OneCBackupMethod.FileArchive, Source = source, DestinationDirectory = Path.Combine(directory, "managed-copies"),
            ScheduleKind = ScheduleKind.SystemStartup };
        var settings = new AgentSettings { AgentId = server.AgentId, Mode = AgentMode.Managed,
            ServerAddress = server.Address, CredentialKey = key, SyncIntervalSeconds = 15 };
        var connection = new ServerMonitoringSettings(server.Address, server.AgentId, key, server.Pin);
        var history = new BackupRunHistory(Path.Combine(directory, "runs.json"));
        var profile = new ManagedAgentProfile(directory, settings, connection);
        var beforeCount = (await history.ReadAsync()).Count;
        async Task Stop()
        {
            if (WindowsAgentService.QueryInfo()?.State == 4) { WindowsAgentService.Stop(executable, directory); await WaitService(1); }
        }
        try
        {
            credentials.Save(key, "agent", server.Token); await credentials.ExportForServiceAsync(key);
            await new AgentSettingsStore(Path.Combine(directory, "agent.json")).SaveAsync(settings);
            await ServerMonitoringDispatcher.SaveSettingsAsync(directory, connection);
            WindowsAgentService.Start(executable, directory); await WaitService(4);
            await Wait(() => Task.FromResult(Volatile.Read(ref server.Gets) > 0), "The service did not read its delegated token or reach pinned HTTPS.");
            Require((await history.ReadAsync()).Count == beforeCount, "A first managed connection without cache executed local jobs.");
            checks.Add("managed-scm-first-connection-without-cache-never-runs-local-job");
            server.Online = true;
            await Wait(async () => (await history.ReadAsync()).Any(r => r.Configuration?.ConnectionId == profile.ConnectionId && r.Status == BackupRunStatus.Succeeded), "The service did not run an accepted managed job.");
            await Wait(() => Task.FromResult(server.Accepted && Interlocked.Read(ref server.ReportedRevision) == 1), "Managed configuration receipt or revision report was not delivered.");
            var copy = (await new BackupCatalog(profile.CatalogPath).ReadAsync()).Single();
            using (var zip = new ZipFile(copy.Backup.Location))
            using (var reader = new StreamReader(zip.GetInputStream(zip.GetEntry("1Cv8.1CD"))))
                Require(await reader.ReadToEndAsync() == "managed service data", "The managed service archive did not restore the original data.");
            Require(!(await File.ReadAllTextAsync(profile.CachePath)).Contains(server.Token, StringComparison.Ordinal), "A bearer token was persisted in the configuration cache.");
            checks.Add("managed-scm-delegated-token-pinned-https-receipt-real-archive-and-revision-report");
            await Stop(); server.Online = false;
            var count = (await history.ReadAsync()).Count;
            WindowsAgentService.Start(executable, directory); await WaitService(4);
            var control = new LocalAgentControlStore(directory); LocalAgentState? state = null;
            await Wait(async () => (state = await control.ReadAsync()) is { CanRunManually: true, ConfigurationRevision: 1 }, "The offline service did not restore its accepted revision.");
            var queue = new LocalManualRunQueue(directory); var command = await queue.EnqueueAsync(state!.SessionId, server.Job.Id);
            await Wait(async () => (await queue.ReadReceiptAsync(command.RequestId))?.Phase == LocalManualRunPhase.Finished, "An offline managed manual command did not finish.");
            Require((await history.ReadAsync()).Count == count + 1, "Offline restart replayed the startup slot or lost its explicit manual run.");
            checks.Add("managed-scm-offline-restart-preserves-slot-and-runs-manual-command");
            await Stop();
            credentials.Save(key + "-new", "agent", server.Token); await credentials.ExportForServiceAsync(key + "-new");
            await new AgentSettingsStore(Path.Combine(directory, "agent.json")).SaveAsync(settings with { CredentialKey = key + "-new" });
            await ServerMonitoringDispatcher.SaveSettingsAsync(directory, connection with { CredentialKey = key + "-new" });
            WindowsAgentService.Start(executable, directory); await WaitService(4);
            await Wait(async () => (state = await control.ReadAsync()) is { CanRunManually: true, ConfigurationRevision: null, Phase: LocalAgentPhase.Running }, "The new enrollment did not start without an inherited cache.");
            var rejected = await queue.EnqueueAsync(state!.SessionId, server.Job.Id);
            await Wait(async () => (await queue.ReadReceiptAsync(rejected.RequestId))?.Phase == LocalManualRunPhase.Rejected, "A new enrollment reused the prior job or local fallback.");
            Require((await history.ReadAsync()).Count == count + 1 && File.Exists(copy.Backup.Location), "Changing enrollment lost archives or executed an inherited job.");
            var afterJobs = await File.ReadAllBytesAsync(Path.Combine(directory, "jobs.json"));
            Require(jobs.SequenceEqual(afterJobs), "Managed service rewrote standalone jobs.");
            checks.Add("managed-scm-new-enrollment-isolates-cache-and-preserves-standalone-profile");
        }
        finally
        {
            await Stop();
            await File.WriteAllBytesAsync(Path.Combine(directory, "agent.json"), originalSettings);
            if (originalMonitoring is null) File.Delete(monitorPath); else await File.WriteAllBytesAsync(monitorPath, originalMonitoring);
            credentials.Delete(key); credentials.Delete(key + "-new");
        }
    }
}
