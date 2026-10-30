using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

internal static class PostgreSqlTlsFixture
{
    public static async Task CreateAsync(string root, string cluster)
    {
        using var authorityKey = RSA.Create(2048);
        var authorityRequest = new CertificateRequest("CN=SP Backup isolated test CA", authorityKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        authorityRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        authorityRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        var since = DateTimeOffset.UtcNow.AddMinutes(-5);
        using var authority = authorityRequest.CreateSelfSigned(since, since.AddDays(2));
        await File.WriteAllTextAsync(Path.Combine(root, "ca.pem"), authority.ExportCertificatePem());
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=isolated PostgreSQL", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using var server = request.Create(authority, since, since.AddDays(1), RandomNumberGenerator.GetBytes(16));
        await File.WriteAllTextAsync(Path.Combine(cluster, "server.crt"), server.ExportCertificatePem());
        await File.WriteAllTextAsync(Path.Combine(cluster, "server.key"), key.ExportPkcs8PrivateKeyPem());
        using var unrelatedKey = RSA.Create(2048);
        using var unrelated = new CertificateRequest("CN=Unrelated test CA", unrelatedKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(since, since.AddDays(2));
        await File.WriteAllTextAsync(Path.Combine(root, "wrong-ca.pem"), unrelated.ExportCertificatePem());
    }

}
