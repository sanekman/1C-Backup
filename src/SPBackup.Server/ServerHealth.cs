using System.Security.Cryptography.X509Certificates;

namespace SPBackup.Server;

public enum DataDiskState { Unknown, Healthy, Low, Critical }
public enum ServerCertificateState { External, Valid, Expiring, Expired, NotYetValid }

public sealed record ServerHealthSnapshot(DateTimeOffset CheckedUtc, long? FreeDataDiskBytes, long? TotalDataDiskBytes,
    DataDiskState DiskState, DateTimeOffset? CertificateNotBeforeUtc, DateTimeOffset? CertificateNotAfterUtc,
    ServerCertificateState CertificateState)
{
    public bool HasWarnings => DiskState != DataDiskState.Healthy
        || CertificateState is ServerCertificateState.Expiring or ServerCertificateState.Expired or ServerCertificateState.NotYetValid;
}

/// <summary>Read-only checks of the data volume and the certificate actually loaded by this process.</summary>
public sealed class ServerHealth(ServerOptions options, IServiceProvider services)
{
    public const long LowDiskBytes = 1024L * 1024 * 1024;
    public const long CriticalDiskBytes = 256L * 1024 * 1024;

    public ServerHealthSnapshot Read()
    {
        long? free = null, total = null;
        try
        {
            // On Unix an absolute directory selects its mounted filesystem, not necessarily '/'.
            var drive = new DriveInfo(Path.GetFullPath(options.DataDirectory));
            free = drive.AvailableFreeSpace; total = drive.TotalSize;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        var certificate = services.GetService<X509Certificate2>();
        return Evaluate(DateTimeOffset.UtcNow, free, total,
            certificate?.NotBefore.ToUniversalTime(), certificate?.NotAfter.ToUniversalTime());
    }

    public static ServerHealthSnapshot Evaluate(DateTimeOffset now, long? free, long? total,
        DateTimeOffset? notBefore, DateTimeOffset? notAfter)
    {
        var disk = free is null or < 0 ? DataDiskState.Unknown : free < CriticalDiskBytes ? DataDiskState.Critical
            : free < LowDiskBytes ? DataDiskState.Low : DataDiskState.Healthy;
        var certificate = notAfter is null ? ServerCertificateState.External
            : notBefore > now ? ServerCertificateState.NotYetValid
            : notAfter <= now ? ServerCertificateState.Expired
            : notAfter <= now.AddDays(30) ? ServerCertificateState.Expiring : ServerCertificateState.Valid;
        return new(now.ToUniversalTime(), free, total, disk, notBefore, notAfter, certificate);
    }
}
