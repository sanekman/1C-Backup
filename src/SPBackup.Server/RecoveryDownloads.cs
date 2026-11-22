using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;

namespace SPBackup.Server;

public sealed record RecoveryDownloadStatus(Guid Id, string Status, DateTimeOffset ExpiresUtc, long Length);
public sealed record RecoveryDownloadOffer(Guid Id, Guid JobId, Guid RunId, int Destination, DateTimeOffset ExpiresUtc, long MaximumBytes);

public sealed class RecoveryDownloads(ServerStore store, TimeProvider? clock = null) : BackgroundService
{
    internal const long MaximumBytes = 512L * 1024 * 1024;
    private const int MaximumRequests = 4;
    private const int CheckedBlock = 1024 * 1024;
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly DirectoryInfo staging = Directory.CreateTempSubdirectory("sp-backup-download-");
    private readonly ConcurrentDictionary<Guid, Transfer> transfers = new();
    private readonly object admission = new();

    private sealed class Transfer(Guid owner, Guid stamp, Guid agent, byte[] generation, RecoveryDownloadOffer offer, string path)
    {
        public Guid Owner { get; } = owner;
        public Guid Stamp { get; } = stamp;
        public Guid Agent { get; } = agent;
        public byte[] Generation { get; } = generation;
        public RecoveryDownloadOffer Offer { get; } = offer;
        public string Path { get; } = path;
        public string Status { get; set; } = "Waiting";
        public long Length { get; set; }
        public volatile bool Cancelled;
    }

    public async Task<Guid> CreateAsync(Guid user, Guid stamp, Guid agent, Guid job, Guid run, int destination, CancellationToken token)
    {
        var generation = await store.DownloadAuthorityAsync(user, stamp, agent, job, run, destination, token)
            ?? throw new ServerRequestException(404, "recovery_point_unavailable");
        var id = Guid.NewGuid();
        var offer = new RecoveryDownloadOffer(id, job, run, destination, time.GetUtcNow().AddMinutes(30), MaximumBytes);
        var transfer = new Transfer(user, stamp, agent, generation, offer, Path.Combine(staging.FullName, $"{id:N}.zip"));
        lock (admission)
        {
            if (transfers.Count >= MaximumRequests || transfers.Values.Any(item => item.Owner == user || item.Agent == agent))
                throw new ServerRequestException(409, "recovery_download_in_progress");
            transfers[id] = transfer;
        }
        try { await store.RecordDownloadAsync(user, agent, "RecoveryDownloadRequested", token); }
        catch { Remove(transfer); throw; }
        return id;
    }

    public IReadOnlyList<(Guid Agent, RecoveryDownloadStatus Status)> PendingForAdministration() => transfers.Values
        .Where(item => !item.Cancelled).Select(item =>
        {
            lock (item) return (item.Agent, new RecoveryDownloadStatus(item.Offer.Id, item.Status, item.Offer.ExpiresUtc, item.Length));
        }).OrderBy(item => item.Item2.ExpiresUtc).ToArray();

    public async Task CancelByAdministratorAsync(Guid user, Guid stamp, Guid id, CancellationToken token)
    {
        if (!await store.ValidateSessionAsync(user, stamp, ServerAuthentication.Administrator, token)
            || !transfers.TryGetValue(id, out var item) || item.Cancelled)
            throw new ServerRequestException(404, "recovery_download_unavailable");
        Remove(item);
        await store.RecordDownloadAsync(user, item.Agent, "RecoveryDownloadCancelled", token);
    }

    public Task<RecoveryDownloadStatus?> ActiveAsync(Guid user, Guid stamp, CancellationToken token)
    {
        var active = transfers.Values.FirstOrDefault(item => item.Owner == user && item.Stamp == stamp);
        return active is null ? Task.FromResult<RecoveryDownloadStatus?>(null) : StatusAsync(user, stamp, active.Offer.Id, token);
    }

    public async Task<RecoveryDownloadStatus?> StatusAsync(Guid user, Guid stamp, Guid id, CancellationToken token)
    {
        if (!transfers.TryGetValue(id, out var item) || item.Owner != user || item.Stamp != stamp || !await AuthorizedAsync(item, token))
            return null;
        lock (item) return new(id, item.Status, item.Offer.ExpiresUtc, item.Length);
    }

    public async Task CancelAsync(Guid user, Guid stamp, Guid id, CancellationToken token)
    {
        if (!transfers.TryGetValue(id, out var item) || item.Owner != user || item.Stamp != stamp || !await AuthorizedAsync(item, token))
            throw new ServerRequestException(404, "recovery_download_unavailable");
        Remove(item);
        await store.RecordDownloadAsync(user, item.Agent, "RecoveryDownloadCancelled", token);
    }

    public async Task<RecoveryDownloadOffer?> PollAsync(Guid agent, byte[] generation, CancellationToken token)
    {
        var item = transfers.Values.FirstOrDefault(item => item.Agent == agent && generation.AsSpan().SequenceEqual(item.Generation));
        if (item is null || !await AuthorizedAsync(item, token)) return null;
        lock (item)
        {
            if (item.Cancelled || item.Status is not ("Waiting" or "Preparing")) return null;
            item.Status = "Preparing";
            return item.Offer;
        }
    }

    public async Task FailAsync(Guid agent, byte[] generation, Guid id, CancellationToken token)
    {
        var item = await AgentTransferAsync(agent, generation, id, token);
        lock (item) if (item.Status == "Preparing") item.Status = "Failed";
    }

    public async Task UploadAsync(Guid agent, byte[] generation, Guid id, long length, string sha256, Stream body, CancellationToken token)
    {
        var item = await AgentTransferAsync(agent, generation, id, token);
        if (length is < 1 or > MaximumBytes || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new ServerRequestException(400, "invalid_recovery_archive");
        lock (item)
        {
            if (item.Cancelled || item.Status != "Preparing") throw new ServerRequestException(409, "recovery_upload_state");
            item.Status = "Uploading";
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        var remaining = item.Offer.ExpiresUtc - time.GetUtcNow();
        deadline.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        try
        {
            using var checksum = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = new FileStream(item.Path, FileMode.CreateNew, FileAccess.Write, FileShare.Delete, 8192, FileOptions.Asynchronous))
            {
                await CopyCheckedAsync(item, body, file, length, deadline.Token, checksum);
                await file.FlushAsync(deadline.Token);
            }
            if (!CryptographicOperations.FixedTimeEquals(checksum.GetHashAndReset(), Convert.FromHexString(sha256)))
                throw new ServerRequestException(400, "recovery_hash_mismatch");
            VerifyArchive(item.Path);
            if (!await AuthorizedAsync(item, deadline.Token)) throw new ServerRequestException(404, "recovery_download_unavailable");
            lock (item) { item.Length = length; item.Status = "Ready"; }
        }
        catch
        {
            TryDelete(item.Path);
            lock (item) if (item.Status == "Uploading") item.Status = "Failed";
            throw;
        }
        finally { if (item.Cancelled) Remove(item); }
    }

    private static void VerifyArchive(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (zip.Entries.Count is < 2 or > 366) throw new InvalidDataException("Invalid number of recovery entries.");
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (!names.Add(name) || entry.Length <= 0 ||
                (name == "recovery.json" ? entry.Length > 512 * 1024 : name.Length != 36 ||
                    !name.EndsWith(".zip", StringComparison.Ordinal) || !Guid.TryParseExact(name[..32], "N", out _)))
                throw new InvalidDataException("Invalid recovery entry.");
        }
        if (!names.Contains("recovery.json") || zip.Entries.Sum(entry => entry.Length) > MaximumBytes)
            throw new InvalidDataException("Invalid recovery manifest or size.");
    }

    public async Task SendAsync(Guid user, Guid stamp, Guid id, HttpContext context)
    {
        var token = context.RequestAborted;
        if (!transfers.TryGetValue(id, out var item) || item.Owner != user || item.Stamp != stamp || !await AuthorizedAsync(item, token))
            throw new ServerRequestException(404, "recovery_download_unavailable");
        lock (item)
        {
            if (item.Cancelled || item.Status != "Ready") throw new ServerRequestException(404, "recovery_download_unavailable");
            // One reader per request also bounds simultaneous read handles and disk use.
            item.Status = "Sending";
        }
        try
        {
            await using var file = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 8192, FileOptions.Asynchronous);
            var range = context.Request.GetTypedHeaders().Range;
            var length = file.Length;
            long start = 0, end = length - 1;
            context.Response.Headers.AcceptRanges = "bytes";
            if (context.Request.Headers.ContainsKey("Range"))
            {
                if (range?.Unit.Value != "bytes" || range.Ranges.Count != 1)
                { RangeUnavailable(context, length); return; }
                var part = range.Ranges.Single();
                start = part.From ?? Math.Max(0, length - part.To!.Value);
                end = part.From is null ? end : Math.Min(end, part.To ?? end);
                if (start > end) { RangeUnavailable(context, length); return; }
                context.Response.StatusCode = StatusCodes.Status206PartialContent;
                context.Response.Headers.ContentRange = $"bytes {start}-{end}/{length}";
            }
            await store.RecordDownloadAsync(user, item.Agent, "RecoveryDownloadOpened", token);
            if (!await AuthorizedAsync(item, token)) throw new ServerRequestException(404, "recovery_download_unavailable");
            context.Response.ContentType = "application/zip";
            context.Response.Headers.ContentDisposition = "attachment; filename=\"SP-Backup-recovery.zip\"";
            context.Response.ContentLength = end - start + 1;
            file.Position = start;
            await CopyCheckedAsync(item, file, context.Response.Body, end - start + 1, token);
        }
        finally
        {
            lock (item) if (item.Status == "Sending") item.Status = "Ready";
            if (item.Cancelled) Remove(item);
        }
    }

    private static void RangeUnavailable(HttpContext context, long length)
    {
        context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
        context.Response.Headers.ContentRange = $"bytes */{length}";
    }

    private async Task CopyCheckedAsync(Transfer item, Stream source, Stream target, long length, CancellationToken token, IncrementalHash? hash = null)
    {
        var buffer = new byte[CheckedBlock];
        while (length > 0)
        {
            var bytes = (int)Math.Min(buffer.Length, length);
            await source.ReadExactlyAsync(buffer.AsMemory(0, bytes), token);
            if (!await AuthorizedAsync(item, token)) throw new ServerRequestException(404, "recovery_download_unavailable");
            await target.WriteAsync(buffer.AsMemory(0, bytes), token);
            hash?.AppendData(buffer, 0, bytes);
            length -= bytes;
        }
    }

    public async Task RequireAgentAccessAsync(Guid agent, byte[] generation, Guid id, CancellationToken token) =>
        _ = await AgentTransferAsync(agent, generation, id, token);

    private async Task<Transfer> AgentTransferAsync(Guid agent, byte[] generation, Guid id, CancellationToken token)
    {
        if (!transfers.TryGetValue(id, out var item) || item.Agent != agent || !generation.AsSpan().SequenceEqual(item.Generation)
            || !await AuthorizedAsync(item, token)) throw new ServerRequestException(404, "recovery_download_unavailable");
        return item;
    }

    private async Task<bool> AuthorizedAsync(Transfer item, CancellationToken token)
    {
        if (item.Cancelled) return false;
        if (item.Offer.ExpiresUtc <= time.GetUtcNow()) { Remove(item); return false; }
        var current = await store.DownloadAuthorityAsync(item.Owner, item.Stamp, item.Agent, item.Offer.JobId, item.Offer.RunId,
            item.Offer.Destination, token);
        return current is not null && current.AsSpan().SequenceEqual(item.Generation) && !item.Cancelled;
    }

    private void Remove(Transfer item)
    {
        lock (item)
        {
            item.Cancelled = true;
            if (item.Status is "Uploading" or "Sending") return;
            transfers.TryRemove(item.Offer.Id, out _);
        }
        TryDelete(item.Path);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Retried by cleanup. */ }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var item in transfers.Values)
                {
                    try { if (!await AuthorizedAsync(item, stoppingToken)) Remove(item); }
                    catch (Npgsql.NpgsqlException) { if (item.Offer.ExpiresUtc <= time.GetUtcNow()) Remove(item); }
                }
                foreach (var path in Directory.EnumerateFiles(staging.FullName))
                    if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id) && !transfers.ContainsKey(id))
                        TryDelete(path);
            }
        }
        finally
        {
            foreach (var item in transfers.Values) Remove(item);
            try { staging.Delete(recursive: true); }
            catch (IOException) { }
        }
    }
}
