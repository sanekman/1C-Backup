using System.Net.Security;
using System.Security.Cryptography;

namespace SPBackup.Core;

/// <summary>Shared HTTPS policy for monitoring and configuration. Pinning never ignores host names or validity dates.</summary>
internal static class ServerHttpsTransport
{
    internal static HttpMessageHandler Create(string certificateSha256) => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (_, cert, _, errors) => cert is not null &&
            (certificateSha256.Length == 0 ? errors == SslPolicyErrors.None :
            (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None &&
            DateTime.UtcNow >= cert.NotBefore.ToUniversalTime() && DateTime.UtcNow <= cert.NotAfter.ToUniversalTime() &&
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.RawData), Convert.FromHexString(certificateSha256)))
    };
}
