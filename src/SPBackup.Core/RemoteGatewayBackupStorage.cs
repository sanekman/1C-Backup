using SPBackup.Remote;

namespace SPBackup.Core;

public sealed record RemoteGatewaySettings
{
    public Guid GatewayId { get; init; }
    public string Endpoint { get; init; } = "";
    public string CredentialKey { get; init; } = "";
    public string PeerCertificate { get; init; } = "";
    public IReadOnlyList<string> Validate()
    {
        try
        {
            RemoteConfiguration.CheckEndpoint(Endpoint);
            if (GatewayId == Guid.Empty || !Guid.TryParseExact(CredentialKey, "N", out _) || PeerCertificate.Length is < 1 or > 16384) throw new InvalidDataException();
            using var peer = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(Convert.FromBase64String(PeerCertificate));
            return [];
        }
        catch { return ["Импортируйте файл подключения шлюза хранилища."]; }
    }
    public static Guid ObjectId(BackupJob job, StoredBackup backup)
    {
        if (job.RemoteGateway is null || !Uri.TryCreate(backup.Location, UriKind.Absolute, out var uri) || uri.Scheme != "onec-remote"
            || uri.Host != job.RemoteGateway.GatewayId.ToString("N") || uri.Port != -1 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Копия не принадлежит этому шлюзу.");
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[0] != job.Id.ToString("N") || !Guid.TryParseExact(parts[1], "N", out var id)) throw new InvalidDataException("Копия не принадлежит этому заданию.");
        return id;
    }
}
public sealed class RemoteGatewayBackupStorage(RemoteStorageClient client) : IBackupStorage
{
    public Task PrepareAsync(BackupJob job, CancellationToken cancellationToken = default) => client.ProbeAsync(cancellationToken);
    public async Task<StoredBackup> StoreAsync(string completedBackupPath, BackupJob job, CancellationToken cancellationToken = default)
    {
        BackupProgressContext.Report(BackupStage.ConnectingStorage);
        var id = Guid.NewGuid();
        var result = await client.PutAsync(job.Id, id, completedBackupPath, cancellationToken,
            BackupProgressContext.Bytes(BackupStage.CopyingToStorage, new FileInfo(completedBackupPath).Length));
        return new($"onec-remote://{job.RemoteGateway!.GatewayId:N}/{job.Id:N}/{id:N}", result.Length, result.Sha256)
            { OriginalFileName = Path.GetFileName(completedBackupPath), ArchivePasswordKey = job.ArchivePasswordKey };
    }
    public Task CopyToAsync(StoredBackup backup, BackupJob job, Stream destination, CancellationToken cancellationToken = default) =>
        client.GetAsync(job.Id, RemoteGatewaySettings.ObjectId(job, backup), backup.Length, backup.Sha256, destination, cancellationToken);
    public Task DeleteAsync(StoredBackup backup, BackupJob job, CancellationToken cancellationToken = default) =>
        client.DeleteAsync(job.Id, RemoteGatewaySettings.ObjectId(job, backup), backup.Length, backup.Sha256, cancellationToken);
    public async Task VerifyAsync(StoredBackup backup, BackupJob job, CancellationToken cancellationToken = default)
    {
        var value = await client.StatAsync(job.Id, RemoteGatewaySettings.ObjectId(job, backup), cancellationToken);
        if (value.Length != backup.Length || !value.Sha256.Equals(backup.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Удалённая копия изменилась.");
    }
}
