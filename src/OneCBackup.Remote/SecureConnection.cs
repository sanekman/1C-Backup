using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OneCBackup.Remote;

internal sealed class WebSocketStream(WebSocket socket) : Stream
{
    private readonly SemaphoreSlim writes = new(1, 1);
    public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { } public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        if (buffer.Length == 0) return 0;
        var read = await socket.ReceiveAsync(buffer, token);
        if (read.MessageType == WebSocketMessageType.Close) return 0;
        if (read.MessageType != WebSocketMessageType.Binary) throw new IOException("Unexpected tunnel message");
        return read.Count;
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
    {
        await writes.WaitAsync(token);
        try { await socket.SendAsync(buffer, WebSocketMessageType.Binary, true, token); }
        finally { writes.Release(); }
    }
    protected override void Dispose(bool disposing) { if (disposing) socket.Dispose(); base.Dispose(disposing); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
}
public static class SecureConnection
{
    private static readonly HttpMessageInvoker transport = new(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(20) });
    internal static async Task ConnectSocketAsync(ClientWebSocket socket, Uri endpoint, CancellationToken token)
    {
        try { await socket.ConnectAsync(endpoint, transport, token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { throw new HttpRequestException("WebSocket connection failed", error, socket.HttpStatusCode == 0 ? null : socket.HttpStatusCode); }
    }
    internal static ClientWebSocket Socket(string access)
    {
        if (access.Length is < 32 or > 1024 || access.Any(char.IsControl)) throw new InvalidDataException("Invalid access token");
        var socket = new ClientWebSocket(); socket.Options.SetRequestHeader("Authorization", "Bearer " + access);
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.HttpVersion = System.Net.HttpVersion.Version11; socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20); socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20); return socket;
    }
    internal static Uri Endpoint(string endpoint, string path) => new(new Uri(endpoint), path);
    private static bool Validate(X509Certificate? certificate, X509Certificate2 trusted, SslPolicyErrors errors)
    {
        if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0
            || !CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256), trusted.GetCertHash(HashAlgorithmName.SHA256))) return false;
        using var peer = new X509Certificate2(certificate);
        using var chain = new X509Chain(); chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(trusted); chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(peer);
    }
    public static async Task<SslStream> ConnectAsync(ClientPairing pairing, CancellationToken token, bool allowLocalEndpoint = false)
    {
        RemoteConfiguration.CheckEndpoint(pairing.Endpoint, allowLocalEndpoint);
        var socket = Socket(pairing.AccessToken);
        var stream = new SslStream(new WebSocketStream(socket), false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await ConnectSocketAsync(socket, Endpoint(pairing.Endpoint, "/remote/client/" + pairing.GatewayId), timeout.Token);
            // Schannel needs an accessible key container. DefaultKeySet creates temporary persisted keys
            // owned by this account and removes them on disposal; Linux containers keep keys in memory.
            using var own = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(pairing.Certificate), pairing.CertificatePassword,
                OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
            using var peer = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(pairing.PeerCertificate));
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "onec-gateway",
                AllowRenegotiation = false,
                ClientCertificates = new X509CertificateCollection { own }, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (_, certificate, _, errors) => Validate(certificate, peer, errors) }, timeout.Token);
            return stream;
        }
        catch { await stream.DisposeAsync(); throw; }
    }
    internal static async Task<SslStream> AcceptAsync(WebSocket socket, X509Certificate2 own, X509Certificate2 peer, CancellationToken token)
    {
        var stream = new SslStream(new WebSocketStream(socket), false);
        try
        {
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = own, ClientCertificateRequired = true,
                AllowRenegotiation = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (_, certificate, _, errors) => Validate(certificate, peer, errors) }, token);
            return stream;
        }
        catch { await stream.DisposeAsync(); throw; }
    }
}
