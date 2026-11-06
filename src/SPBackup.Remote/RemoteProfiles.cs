using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace SPBackup.Remote;

public sealed record ClientPairing(Guid GatewayId, string Endpoint, string AccessToken, string Certificate, string CertificatePassword, string PeerCertificate);
public sealed record GatewayOptions(Guid GatewayId, string Endpoint, string AccessTokenFile, string CertificateFile,
    string CertificatePasswordFile, string PeerCertificateFile, string DataDirectory, long QuotaBytes = 100L * 1024 * 1024 * 1024);
public sealed record HubPeer(Guid GatewayId, string TokenSha256);
public sealed record HubOptions
{
    public bool Enabled { get; init; }
    public int Port { get; init; } = 7087;
    public IReadOnlyList<HubPeer> Peers { get; init; } = [];
    public void Validate()
    {
        if (Port is < 1024 or > 65535 || Peers is null || Peers.Count > 1000 || Peers.Any(p => p.GatewayId == Guid.Empty
            || p.TokenSha256.Length != 64 || !p.TokenSha256.All(Uri.IsHexDigit)) || Peers.Select(p => p.GatewayId).Distinct().Count() != Peers.Count)
            throw new InvalidDataException("Invalid hub configuration");
    }
}
public static class RemoteConfiguration
{
    public static string ErrorCode(Exception error)
    {
        var parts = new List<string>();
        for (Exception? current = error; current is not null && parts.Count < 5; current = current.InnerException)
            parts.Add(current.GetType().Name + (current is HttpRequestException http && http.StatusCode is { } status ? ":HTTP" + (int)status : ""));
        return string.Join("/", parts);
    }
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static async Task<T> ReadAsync<T>(string path, CancellationToken token = default)
    {
        if (new FileInfo(path).Length > 128 * 1024) throw new InvalidDataException("Configuration too large");
        await using var file = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(file, Json, token) ?? throw new InvalidDataException("Empty configuration");
    }
    public static void CheckEndpoint(string endpoint, bool allowLocalEndpoint = false)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath != "/" || (uri.Scheme != "wss" && !(allowLocalEndpoint && uri.Scheme == "ws" && uri.IsLoopback)))
            throw new InvalidDataException("A WSS endpoint without path, credentials or query is required");
    }
    public static string Secret(string path)
    {
        if (new FileInfo(path).Length > 4096) throw new InvalidDataException("Secret too large");
        var value = File.ReadAllText(path).Trim();
        if (value.Length is < 32 or > 1024 || value.Any(char.IsControl)) throw new InvalidDataException("Invalid secret");
        return value;
    }
    public static void Initialize(string directory, string endpoint)
    {
        CheckEndpoint(endpoint);
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) throw new IOException("Initialization directory must be empty");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var id = Guid.NewGuid(); var access = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var client = Certificate("onec-agent"); using var gateway = Certificate("onec-gateway");
        var protection = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 200_000);
        File.WriteAllBytes(Path.Combine(directory, "gateway.pfx"), gateway.ExportPkcs12(protection, password));
        File.WriteAllBytes(Path.Combine(directory, "agent.pfx"), client.ExportPkcs12(protection, password));
        File.WriteAllText(Path.Combine(directory, "agent.pem"), client.ExportCertificatePem());
        File.WriteAllText(Path.Combine(directory, "gateway.pem"), gateway.ExportCertificatePem());
        File.WriteAllText(Path.Combine(directory, "certificate-password.txt"), password);
        File.WriteAllText(Path.Combine(directory, "access-token.txt"), access);
        File.WriteAllText(Path.Combine(directory, "client-pairing.json"), JsonSerializer.Serialize(new ClientPairing(id, endpoint, access,
            Convert.ToBase64String(client.ExportPkcs12(protection, password)), password, Convert.ToBase64String(gateway.Export(X509ContentType.Cert))), Json));
        File.WriteAllText(Path.Combine(directory, "gateway.json"), JsonSerializer.Serialize(new GatewayOptions(id, endpoint,
            "/config/access-token.txt", "/config/gateway.pfx", "/config/certificate-password.txt", "/config/agent.pem", "/backups"), Json));
        File.WriteAllText(Path.Combine(directory, "hub.json"), JsonSerializer.Serialize(new HubOptions { Enabled = true,
            Peers = [new(id, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(access))))] }, Json));
        if (!OperatingSystem.IsWindows()) foreach (var path in Directory.EnumerateFiles(directory)) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    private static X509Certificate2 Certificate(string name)
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign, true));
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(name); request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(3));
    }
}
