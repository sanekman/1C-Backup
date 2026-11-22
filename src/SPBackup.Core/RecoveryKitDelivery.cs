using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;

namespace SPBackup.Core;

internal sealed class RecoveryKitDelivery(string directory, IFtpCredentialStore credentials, ServerMonitoringSettings? fixedConnection,
    IBackupJobSource jobs, BackupCatalog catalog, IBackupStorage storage, LocalBackupRunner runner, BackupRunHistory history)
{
    private const long MaximumBytes = 512L * 1024 * 1024;
    private sealed record Offer(Guid Id, Guid JobId, Guid RunId, int Destination, DateTimeOffset ExpiresUtc, long MaximumBytes);
    private readonly string temp = Path.Combine(directory, "recovery-delivery");

    internal async Task RunAsync(Action<Exception?> report, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try { await PollAsync(token); report(null); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { report(error); }
        } while (await timer.WaitForNextTickAsync(token));
    }

    private async Task PollAsync(CancellationToken token)
    {
        var connection = await ServerMonitoringDispatcher.LoadSettingsAsync(directory, token);
        if (connection is not { AllowRecoveryKitExport: true } ||
            fixedConnection is not null && !connection.SameEnrollment(fixedConnection)) return;
        using var http = new HttpClient(ServerHttpsTransport.Create(connection.CertificateSha256))
        {
            BaseAddress = new(connection.Address.TrimEnd('/') + "/"),
            Timeout = Timeout.InfiniteTimeSpan
        };
        var secret = await credentials.GetAsync(connection.CredentialKey, token);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret.Password);
        var route = $"api/v1/agents/{connection.AgentId:D}/recovery-downloads";
        Offer? offer;
        using (var poll = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            poll.CancelAfter(TimeSpan.FromSeconds(10));
            using var result = await http.GetAsync(route, poll.Token);
            if (result.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound) return;
            result.EnsureSuccessStatusCode();
            offer = await result.Content.ReadFromJsonAsync<Offer>(poll.Token);
        }
        var remaining = offer?.ExpiresUtc - DateTimeOffset.UtcNow;
        if (offer is null || offer.Id == Guid.Empty || offer.JobId == Guid.Empty || offer.RunId == Guid.Empty
            || offer.Destination is < 0 or >= 100 || offer.MaximumBytes is < 1 or > MaximumBytes
            || remaining <= TimeSpan.Zero || remaining > TimeSpan.FromMinutes(31)) return;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        lifetime.CancelAfter(remaining!.Value);
        var request = $"{route}/{offer.Id:D}";
        var watch = WatchAsync(http, connection, request, lifetime);
        var work = Path.Combine(temp, offer.Id.ToString("N") + "-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            FileBackupRestorer.RejectAncestorReparse(temp);
            PrivateBackupDirectory.Create(work);
            var archive = await runner.TryPrepareRecoveryKitAsync(() => PrepareAsync(connection, offer, work, lifetime.Token), lifetime.Token);
            if (archive is null) return;
            await using var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
            if (stream.Length > offer.MaximumBytes) throw new InvalidDataException("Комплект превышает допустимый размер.");
            var sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, lifetime.Token));
            stream.Position = 0;
            using var content = new StreamContent(stream);
            content.Headers.ContentLength = stream.Length;
            content.Headers.ContentType = new("application/zip");
            using var upload = new HttpRequestMessage(HttpMethod.Put, request + "/file") { Content = content };
            upload.Headers.Add("X-Recovery-Sha256", sha);
            using var response = await http.SendAsync(upload, lifetime.Token);
            response.EnsureSuccessStatusCode();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested && !token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException or InvalidDataException or ArgumentException)
        {
            using var failure = CancellationTokenSource.CreateLinkedTokenSource(token);
            failure.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await http.PostAsync(request + "/failed", null, failure.Token);
            DiagnosticLog.Current.Write(DiagnosticEvent.MonitoringFailed, DiagnosticLevel.Warning, error);
        }
        finally
        {
            await lifetime.CancelAsync();
            await watch;
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    private async Task WatchAsync(HttpClient http, ServerMonitoringSettings connection, string request, CancellationTokenSource lifetime)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                using var check = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                check.CancelAfter(TimeSpan.FromSeconds(5));
                var configured = await ServerMonitoringDispatcher.LoadSettingsAsync(directory, check.Token);
                if (configured is not { AllowRecoveryKitExport: true } || !connection.SameEnrollment(configured)) break;
                using var result = await http.GetAsync(request, check.Token);
                if (result.StatusCode != HttpStatusCode.NoContent) break;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        catch (Exception error) when (error is IOException or HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
        { DiagnosticLog.Current.Write(DiagnosticEvent.MonitoringFailed, DiagnosticLevel.Warning, error); }
        await lifetime.CancelAsync();
    }

    private async Task<string> PrepareAsync(ServerMonitoringSettings connection, Offer offer, string work, CancellationToken token)
    {
        var job = (await jobs.LoadAsync(token)).SingleOrDefault(item => item.Id == offer.JobId && item.IsFileSnapshot)
            ?? throw new InvalidDataException("Задание копирования файлов недоступно.");
        var scope = ManagedAgentProfile.Identity(new AgentSettings { Mode = AgentMode.Managed, AgentId = connection.AgentId,
            ServerAddress = connection.Address, CredentialKey = connection.CredentialKey });
        var run = (await history.ReadAsync(token)).SingleOrDefault(item => item.RunId == offer.RunId && item.JobId == offer.JobId &&
            (fixedConnection is null ? item.Configuration is null : item.Configuration?.ConnectionId == scope))
            ?? throw new InvalidDataException("Запуск недоступен.");
        var referenced = run.Destinations is { } destinations
            ? destinations.ElementAtOrDefault(offer.Destination)?.Record
            : null;
        var backup = referenced?.Backup ?? (run.Destinations is null && offer.Destination == 0 ? run.Backup : null);
        if (backup?.FileSnapshot is null) throw new InvalidDataException("Копия для хранилища недоступна.");
        var records = await catalog.ReadAsync(token);
        var point = records.SingleOrDefault(item => item.JobId == offer.JobId && (referenced is null || item.Id == referenced.Id)
            && item.Backup.Location == backup.Location && item.Backup.Length == backup.Length && item.Backup.Sha256 == backup.Sha256
            && item.Backup.FileSnapshot?.SnapshotId == backup.FileSnapshot.SnapshotId)
            ?? throw new InvalidDataException("Копия отсутствует в каталоге.");
        var kit = await new FileRecoveryKit(storage).ExportAsync(point, job, catalog, work, token: token, maximumBytes: offer.MaximumBytes);
        var zip = Path.Combine(work, "recovery.zip");
        await ZipFile.CreateFromDirectoryAsync(kit, zip, CompressionLevel.NoCompression, includeBaseDirectory: false, token);
        return zip;
    }
}
