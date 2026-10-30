using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class AgentConnectionHttpsTests
{
    private sealed class Store : IFtpCredentialStore
    {
        public Task<NetworkCredential> GetAsync(string key, CancellationToken token = default) =>
            Task.FromResult(new NetworkCredential("agent", "fixture-agent-token"));
    }

    private sealed class Server : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(30));
        private readonly X509Certificate2 certificate;
        private readonly Task loop;
        private readonly bool redirect;
        public readonly Guid AgentId = Guid.NewGuid();
        public readonly List<string> Authorization = [];
        public string Address { get; }
        public string Thumbprint => Convert.ToHexString(SHA256.HashData(certificate.RawData));

        public Server(string fault = "valid", bool redirect = false)
        {
            this.redirect = redirect;
            using var rsa = RSA.Create(2048);
            var name = fault == "name" ? "other.example.test" : "localhost";
            var request = new CertificateRequest("CN=" + name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(name); request.CertificateExtensions.Add(san.Build());
            var now = DateTimeOffset.UtcNow;
            using var issued = request.CreateSelfSigned(fault == "future" ? now.AddDays(1) : now.AddDays(-2),
                fault == "expired" ? now.AddDays(-1) : now.AddDays(2));
            var pfx = issued.Export(X509ContentType.Pfx);
            try { certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.DefaultKeySet); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
            listener.Start(); Address = $"https://localhost:{((IPEndPoint)listener.LocalEndpoint).Port}/prefix/";
            loop = Serve();
        }

        private async Task Serve()
        {
            try
            {
                while (true)
                {
                    using var connection = await listener.AcceptTcpClientAsync(stop.Token);
                    await using var stream = new SslStream(connection.GetStream());
                    try
                    {
                        await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, stop.Token);
                        var bytes = new List<byte>(); var one = new byte[1];
                        while (bytes.Count < 32768)
                        {
                            if (await stream.ReadAsync(one, stop.Token) == 0) throw new IOException("Incomplete fixture request");
                            bytes.Add(one[0]);
                            if (bytes.Count >= 4 && bytes.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                        }
                        var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n");
                        Authorization.Add(lines.FirstOrDefault(line => line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) ?? "");
                        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { agentId = AgentId, revision = 1, jobs = Array.Empty<object>(), protocolVersion = 1 }));
                        var headers = redirect
                            ? $"HTTP/1.1 302 Found\r\nLocation: {Address}redirected\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                            : $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), stop.Token);
                        if (!redirect) await stream.WriteAsync(body, stop.Token);
                        await stream.FlushAsync(stop.Token);
                    }
                    catch (Exception error) when (error is IOException or AuthenticationException) { }
                }
            }
            catch (Exception error) when (stop.IsCancellationRequested && error is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }

        public async ValueTask DisposeAsync()
        {
            // Drain token-aware accepts/handshakes before stopping the listener. Schannel
            // may complete a failed handshake concurrently with teardown and enter Accept again.
            stop.Cancel();
            try { await loop; }
            finally { listener.Stop(); certificate.Dispose(); stop.Dispose(); }
        }
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("wrong-pin")]
    [InlineData("system-trust")]
    [InlineData("name")]
    [InlineData("expired")]
    [InlineData("future")]
    public async Task RealHttpsRequiresTrustedOrPinnedCertificateWithValidNameAndDates(string fault)
    {
        await using var server = new Server(fault);
        var pin = fault switch { "wrong-pin" => new string('0', 64), "system-trust" => "", _ => server.Thumbprint };
        var connection = new ServerMonitoringSettings(server.Address, server.AgentId, "test-enrollment", pin);
        var settings = new AgentSettings { AgentId = server.AgentId, Mode = AgentMode.Managed,
            ServerAddress = server.Address, CredentialKey = connection.CredentialKey };
        using var api = new AgentApiClient(connection, new Store()); // Production handler, no TLS test override.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        if (fault == "valid")
        {
            var configuration = await api.GetConfigurationAsync(settings, null, deadline.Token);
            Assert.Equal(server.AgentId, configuration!.AgentId); Assert.Empty(configuration.Jobs);
            Assert.Equal("Authorization: Bearer fixture-agent-token", Assert.Single(server.Authorization));
        }
        else
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => api.GetConfigurationAsync(settings, null, deadline.Token));
            Assert.Empty(server.Authorization);
        }
    }

    [Fact]
    public async Task RealHttpsDoesNotFollowRedirectEvenWithTheSameCertificate()
    {
        await using var server = new Server(redirect: true);
        var connection = new ServerMonitoringSettings(server.Address, server.AgentId, "test-enrollment", server.Thumbprint);
        var settings = new AgentSettings { AgentId = server.AgentId, Mode = AgentMode.Managed,
            ServerAddress = server.Address, CredentialKey = connection.CredentialKey };
        using var api = new AgentApiClient(connection, new Store());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => api.GetConfigurationAsync(settings, null, deadline.Token));
        Assert.Equal(HttpStatusCode.Redirect, error.StatusCode);
        Assert.Single(server.Authorization);
    }
}
