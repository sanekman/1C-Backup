using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using OneCBackup.Remote;

namespace OneCBackup.Core;

[SupportedOSPlatform("windows")]
public sealed class WindowsRemoteGatewayConnections(string directory)
{
    private readonly WindowsFtpCredentialStore secrets = new("RemoteGateway");
    public async Task<RemoteGatewaySettings> ImportAsync(string path)
    {
        var pairing = await RemoteConfiguration.ReadAsync<ClientPairing>(path);
        RemoteConfiguration.CheckEndpoint(pairing.Endpoint);
        if (pairing.GatewayId == Guid.Empty || pairing.CertificatePassword.Length < 32 || pairing.AccessToken.Length < 32
            || pairing.AccessToken.Length > 512 || pairing.AccessToken.Any(char.IsControl)) throw new InvalidDataException("Некорректный файл подключения.");
        var bytes = Convert.FromBase64String(pairing.Certificate);
        using var certificate = X509CertificateLoader.LoadPkcs12(bytes, pairing.CertificatePassword, X509KeyStorageFlags.EphemeralKeySet);
        if (!certificate.HasPrivateKey || certificate.NotAfter <= DateTime.Now) throw new InvalidDataException("Некорректный сертификат подключения.");
        var key = Guid.NewGuid().ToString("N");
        var settings = new RemoteGatewaySettings { GatewayId = pairing.GatewayId, CredentialKey = key,
            Endpoint = pairing.Endpoint, PeerCertificate = pairing.PeerCertificate };
        if (settings.Validate().Count != 0) throw new InvalidDataException("Некорректный файл подключения.");
        Directory.CreateDirectory(Path.Combine(directory, "remote-keys"));
        await File.WriteAllBytesAsync(KeyPath(key), bytes);
        secrets.Save(key, pairing.AccessToken, pairing.CertificatePassword);
        return settings;
    }
    private string KeyPath(string key)
    {
        if (!Guid.TryParseExact(key, "N", out _)) throw new InvalidDataException("Некорректный идентификатор подключения.");
        return Path.Combine(directory, "remote-keys", key + ".pfx");
    }
    public async Task<ClientPairing> GetAsync(RemoteGatewaySettings settings, CancellationToken token)
    {
        if (settings.Validate().Count != 0) throw new InvalidDataException("Некорректное подключение шлюза.");
        var secret = await secrets.GetAsync(settings.CredentialKey, token);
        var path = KeyPath(settings.CredentialKey);
        if (new FileInfo(path).Length > 32768) throw new InvalidDataException("Некорректный размер сертификата.");
        var endpoint = settings.Endpoint;
        var hubPath = Path.Combine(directory, "remote-hub.json");
        if (File.Exists(hubPath))
        {
            var hub = await RemoteConfiguration.ReadAsync<HubOptions>(hubPath, token); hub.Validate();
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret.UserName)));
            if (hub.Enabled && hub.Peers.Any(peer => peer.GatewayId == settings.GatewayId && peer.TokenSha256.Equals(hash, StringComparison.OrdinalIgnoreCase)))
                endpoint = "ws://127.0.0.1:" + hub.Port; // Local route still carries mutual TLS; avoids requiring router NAT loopback.
        }
        return new(settings.GatewayId, endpoint, secret.UserName, Convert.ToBase64String(await File.ReadAllBytesAsync(path, token)), secret.Password, settings.PeerCertificate);
    }
    public async Task ExportForServiceAsync(IReadOnlyList<BackupJob> jobs)
    {
        foreach (var key in jobs.SelectMany(j => j.StorageJobs).Where(j => j.RemoteGateway is not null)
            .Select(j => j.RemoteGateway!.CredentialKey).Distinct()) await secrets.ExportForServiceAsync(key);
    }
}
