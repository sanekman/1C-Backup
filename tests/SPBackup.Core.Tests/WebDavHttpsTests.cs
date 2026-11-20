using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class WebDavHttpsTests
{
    [Fact]
    public async Task RealTlsPropfindAndGetRestoreFullAndIncrementalWithoutDownloadingStableFile()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); request.CertificateExtensions.Add(san.Build());
        using var issued = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        // Schannel cannot serve TLS with an ephemeral RSA key. Import a temporary default-key-set PFX; disposal releases it.
        var pfx = issued.Export(X509ContentType.Pfx);
        using var certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.DefaultKeySet);
        CryptographicOperations.ZeroMemory(pfx);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var dav = new WebDavBackupTests.DavFixture(); dav.Files["данные #%.txt"] = ("first", "\"a1\""); dav.Files["stable.txt"] = ("stable", "\"s1\"");
        var source = $"https://localhost:{port}/data/";
        var server = Task.Run(async () =>
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    using var connection = await listener.AcceptTcpClientAsync(cancellation.Token);
                    await using var stream = new SslStream(connection.GetStream());
                    await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, cancellation.Token);
                    var header = new List<byte>(); var one = new byte[1];
                    while (header.Count < 32768)
                    {
                        if (await stream.ReadAsync(one, cancellation.Token) == 0) throw new IOException("Incomplete request");
                        header.Add(one[0]);
                        if (header.Count >= 4 && header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                    }
                    var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n"); var first = lines[0].Split(' ');
                    using var message = new HttpRequestMessage(new HttpMethod(first[0]), new Uri(new Uri(source), first[1]));
                    var contentLength = 0;
                    foreach (var line in lines.Skip(1).Where(line => line.Contains(':')))
                    {
                        var colon = line.IndexOf(':'); var name = line[..colon]; var value = line[(colon + 1)..].Trim();
                        if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) contentLength = int.Parse(value);
                        else if (!name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)) message.Headers.TryAddWithoutValidation(name, value);
                    }
                    if (contentLength > 65536) throw new IOException("Request too large");
                    if (contentLength > 0)
                    {
                        var body = new byte[contentLength]; await stream.ReadExactlyAsync(body, cancellation.Token); message.Content = new ByteArrayContent(body);
                    }
                    using var response = await dav.Send(message, cancellation.Token);
                    var output = await response.Content.ReadAsByteArrayAsync(cancellation.Token);
                    var responseHeader = $"HTTP/1.1 {(int)response.StatusCode} OK\r\nContent-Length: {output.Length}\r\nConnection: close\r\n";
                    if (response.Headers.ETag is not null) responseHeader += "ETag: " + response.Headers.ETag + "\r\n";
                    responseHeader += "\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(responseHeader), cancellation.Token);
                    await stream.WriteAsync(output, cancellation.Token); await stream.FlushAsync(cancellation.Token);
                }
            }
            catch (Exception ex) when (cancellation.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }, cancellation.Token);
        var root = Path.Combine(Path.GetTempPath(), "onec-dav-tls-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var job = new BackupJob { Kind = DatabaseKind.WebDav, Name = "TLS DAV", Source = source, WebDav = new() { CredentialKey = "fixture" },
                DestinationDirectory = Path.Combine(root, "copies"), FileBackupMode = FileBackupMode.Incremental, KeepLast = 1 };
            WebDavClient Client(BackupJob settings) => new(settings.Source, new NetworkCredential("пользователь", "password"),
                new SocketsHttpHandler { AllowAutoRedirect = false, SslOptions = new() {
                    // Trust only this fixture certificate. Production keeps normal system certificate validation.
                    RemoteCertificateValidationCallback = (_, received, _, _) => received is not null && received.GetCertHashString() == certificate.GetCertHashString() } });
            var catalog = new BackupCatalog(Path.Combine(root, "copies.json")); var storage = new FileBackupStorage();
            var runner = new LocalBackupRunner(new DatabaseBackupExecutor(new BackupProcessRunner()), new BackupCompletion(storage, catalog),
                new BackupRunHistory(Path.Combine(root, "runs.json")), Path.Combine(root, "work"), Client);
            var first = await runner.RunAsync(job, cancellation.Token); Assert.Equal(BackupRunStatus.Succeeded, first.Status);
            Assert.Equal(2, dav.Downloads.Count); dav.Downloads.Clear(); dav.Files["данные #%.txt"] = ("second", "\"a2\"");
            var second = await runner.RunAsync(job, cancellation.Token); Assert.Equal(BackupRunStatus.Succeeded, second.Status);
            Assert.Equal(new[] { "данные #%.txt" }, dav.Downloads);
            var record = (await catalog.ReadAsync()).Single(record => record.Backup == second.Backup);
            var parent = Path.Combine(root, "restored"); Directory.CreateDirectory(parent);
            var restore = await new FileBackupRestorer(storage, catalog, Path.Combine(root, "work")).RestoreAsync(record, job, parent, cancellation.Token);
            Assert.Equal(2, restore.Archives); Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(restore.Directory, "данные #%.txt")));
            Assert.Equal("stable", await File.ReadAllTextAsync(Path.Combine(restore.Directory, "stable.txt")));
        }
        finally { cancellation.Cancel(); listener.Stop(); await server; Directory.Delete(root, true); }
    }
}
