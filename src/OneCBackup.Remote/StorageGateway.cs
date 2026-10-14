using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace OneCBackup.Remote;

public sealed class StorageGateway(GatewayOptions options, bool allowLocalEndpoint = false)
{
    private readonly SemaphoreSlim writes = new(1, 1);
    public async Task RunAsync(CancellationToken token)
    {
        RemoteConfiguration.CheckEndpoint(options.Endpoint, allowLocalEndpoint);
        if (options.GatewayId == Guid.Empty || options.QuotaBytes < 1 || !Path.IsPathFullyQualified(options.DataDirectory)) throw new InvalidDataException("Invalid gateway configuration");
        Directory.CreateDirectory(options.DataDirectory); NoLink(options.DataDirectory);
        using var own = X509CertificateLoader.LoadPkcs12FromFile(options.CertificateFile, RemoteConfiguration.Secret(options.CertificatePasswordFile),
            OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
        using var peer = X509Certificate2.CreateFromPem(File.ReadAllText(options.PeerCertificateFile));
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var control = SecureConnection.Socket(RemoteConfiguration.Secret(options.AccessTokenFile));
                using var connecting = CancellationTokenSource.CreateLinkedTokenSource(token); connecting.CancelAfter(TimeSpan.FromSeconds(20));
                await SecureConnection.ConnectSocketAsync(control, SecureConnection.Endpoint(options.Endpoint, "/remote/control/" + options.GatewayId), connecting.Token);
                Console.WriteLine("gateway_connected");
                using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
                var active = new List<Task>();
                try
                {
                    while (true)
                    {
                        var buffer = new byte[128]; var read = await control.ReceiveAsync(buffer.AsMemory(), session.Token);
                        if (read.MessageType == WebSocketMessageType.Close) break;
                        if (!read.EndOfMessage || read.MessageType != WebSocketMessageType.Text || read.Count != 36
                            || !Guid.TryParse(Encoding.ASCII.GetString(buffer, 0, read.Count), out var offer)) throw new IOException("Invalid offer");
                        active.RemoveAll(task => task.IsCompleted);
                        if (active.Count >= 4) throw new IOException("Too many transfers");
                        active.Add(ServeAsync(offer, own, peer, session.Token));
                    }
                }
                finally { session.Cancel(); await Task.WhenAll(active); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) { Console.Error.WriteLine("gateway_connection_warning:" + RemoteConfiguration.ErrorCode(error)); }
            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
    }
    private async Task ServeAsync(Guid offer, X509Certificate2 own, X509Certificate2 peer, CancellationToken token)
    {
        try
        {
            using var socket = SecureConnection.Socket(RemoteConfiguration.Secret(options.AccessTokenFile));
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token); handshake.CancelAfter(TimeSpan.FromSeconds(20));
            await SecureConnection.ConnectSocketAsync(socket, SecureConnection.Endpoint(options.Endpoint, $"/remote/gateway/{options.GatewayId}/{offer}"), handshake.Token);
            await using var stream = await SecureConnection.AcceptAsync(socket, own, peer, handshake.Token);
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(token); idle.CancelAfter(TimeSpan.FromSeconds(30));
            var request = await RemoteProtocol.ReadAsync<RemoteRequest>(stream, idle.Token);
            if (request.JobId == Guid.Empty || request.ObjectId == Guid.Empty) { await RemoteProtocol.WriteAsync(stream, new RemoteReply(false, "invalid_id"), token); return; }
            NoLink(options.DataDirectory);
            if (request.Method == "ping") { await RemoteProtocol.WriteAsync(stream, new RemoteReply(true), token); await Acknowledged(stream, token); return; }
            var directory = Path.Combine(options.DataDirectory, request.JobId.ToString("N"));
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            NoLink(directory);
            var path = Path.Combine(directory, request.ObjectId.ToString("N") + ".bin");
            switch (request.Method)
            {
                case "put": await PutAsync(stream, path, request, token); break;
                case "get":
                    if (!File.Exists(path)) { await RemoteProtocol.WriteAsync(stream, new RemoteReply(false, "not_found"), token); break; }
                    NoLink(path);
                    await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true))
                    {
                        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token)); file.Position = 0;
                        await RemoteProtocol.WriteAsync(stream, new RemoteReply(true, Length: file.Length, Sha256: hash), token);
                        await RemoteProtocol.CopyExactAsync(file, stream, file.Length, token); await stream.FlushAsync(token);
                    }
                    break;
                case "stat":
                    if (!File.Exists(path)) await RemoteProtocol.WriteAsync(stream, new RemoteReply(false, "not_found"), token);
                    else { NoLink(path); await using var file = File.OpenRead(path); var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token)); await RemoteProtocol.WriteAsync(stream, new RemoteReply(true, Length: file.Length, Sha256: hash), token); }
                    break;
                case "delete":
                    await writes.WaitAsync(token);
                    try
                    {
                        if (File.Exists(path))
                        {
                            NoLink(path); await using (var file = File.OpenRead(path))
                                if (file.Length != request.Length || !Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(request.Sha256, StringComparison.OrdinalIgnoreCase))
                                { await RemoteProtocol.WriteAsync(stream, new RemoteReply(false, "object_changed"), token); break; }
                            File.Delete(path);
                        }
                        var metadataPath = Path.ChangeExtension(path, ".json");
                        if (File.Exists(metadataPath)) { NoLink(metadataPath); File.Delete(metadataPath); }
                        await RemoteProtocol.WriteAsync(stream, new RemoteReply(true), token);
                    }
                    finally { writes.Release(); }
                    break;
                default: await RemoteProtocol.WriteAsync(stream, new RemoteReply(false, "invalid_method"), token); break;
            }
            await Acknowledged(stream, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { Console.Error.WriteLine("gateway_transfer_warning:" + error.GetType().Name); }
    }
    private static async Task Acknowledged(Stream stream, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var reply = await RemoteProtocol.ReadAsync<RemoteReply>(stream, timeout.Token);
        if (!reply.Success || reply.Code != "received") throw new IOException("Missing receipt acknowledgement");
    }
    private async Task PutAsync(Stream stream, string path, RemoteRequest request, CancellationToken token)
    {
        if (request.Length < 1 || request.Length > options.QuotaBytes || request.Sha256.Length != 64 || !request.Sha256.All(Uri.IsHexDigit)
            || request.OriginalFileName.Length > 512 || request.OriginalFileName.Any(char.IsControl)
            || request.OriginalFileName.Contains('/') || request.OriginalFileName.Contains('\\'))
        { await RemoteProtocol.WriteAsync(stream, new RemoteReply(false, "invalid_object"), token); return; }
        await writes.WaitAsync(token);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
        var metadataPath = Path.ChangeExtension(path, ".json");
        var metadata = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { request.JobId, request.ObjectId,
            request.OriginalFileName, request.Length, request.Sha256, ReceivedUtc = DateTimeOffset.UtcNow });
        async Task SaveMetadata()
        {
            var pendingMetadata = metadataPath + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                var creation = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
                if (!OperatingSystem.IsWindows()) creation.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var file = new FileStream(pendingMetadata, creation)) { await file.WriteAsync(metadata, token); await file.FlushAsync(token); file.Flush(true); }
                File.Move(pendingMetadata, metadataPath, false);
            }
            finally { if (File.Exists(pendingMetadata)) File.Delete(pendingMetadata); }
        }
        try
        {
            if (File.Exists(path))
            {
                NoLink(path); await using var existing = File.OpenRead(path);
                var same = existing.Length == request.Length && Convert.ToHexString(await SHA256.HashDataAsync(existing, token)).Equals(request.Sha256, StringComparison.OrdinalIgnoreCase);
                if (same && !File.Exists(metadataPath)) await SaveMetadata();
                await RemoteProtocol.WriteAsync(stream, new RemoteReply(same, same ? "present" : "object_changed", existing.Length, request.Sha256), token); return;
            }
            long used = 0;
            foreach (var file in Directory.EnumerateFiles(options.DataDirectory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
                used = checked(used + new FileInfo(file).Length);
            if (request.Length > options.QuotaBytes - metadata.Length || used > options.QuotaBytes - request.Length - metadata.Length) { await RemoteProtocol.WriteAsync(stream, new RemoteReply(false, "quota_exceeded"), token); return; }
            await RemoteProtocol.WriteAsync(stream, new RemoteReply(true, "ready"), token);
            var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                Share = FileShare.None, BufferSize = 128 * 1024, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(temporary, fileOptions))
            {
                var hash = await RemoteProtocol.CopyExactAsync(stream, file, request.Length, token);
                if (!hash.Equals(request.Sha256, StringComparison.OrdinalIgnoreCase)) { await RemoteProtocol.WriteAsync(stream, new RemoteReply(false, "hash_mismatch"), token); return; }
                await file.FlushAsync(token); file.Flush(true);
            }
            File.Move(temporary, path, false);
            await SaveMetadata();
            await RemoteProtocol.WriteAsync(stream, new RemoteReply(true, Length: request.Length, Sha256: request.Sha256), token);
            Console.WriteLine("gateway_object_committed");
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } finally { writes.Release(); } }
    }
    private static void NoLink(string path) { if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Links are not accepted in storage paths"); }
}
