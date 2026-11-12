using System.Net;

namespace SPBackup.Core;

public interface IFtpCredentialStore
{
    Task<NetworkCredential> GetAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>Selects storage from each immutable job. Credentials are never part of the job document.</summary>
public sealed class RoutedBackupStorage(IFtpCredentialStore credentials, string verificationDirectory,
    Func<NetworkCredential, string, IBackupStorage>? ftpFactory = null, IFtpCredentialStore? googleCredentials = null,
    Func<GoogleDriveCredentials, IBackupStorage>? googleFactory = null,
    Func<RemoteGatewaySettings, CancellationToken, Task<SPBackup.Remote.ClientPairing>>? remoteConnections = null) : IBackupStorage
{
    private readonly FileBackupStorage files = new();
    private static readonly HttpClient googleHttp = GoogleDriveClient.CreateHttpClient();

    public async Task PrepareAsync(BackupJob job, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var errors = job.Validate();
        if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
        if (job.StorageKind == StorageKind.Ftp) await ResolveAsync(job, cancellationToken);
        if (job.NetworkShare is not null) await ResolveAsync(job, cancellationToken);
        if (job.StorageKind == StorageKind.GoogleDrive) await (await ResolveAsync(job, cancellationToken)).PrepareAsync(job, cancellationToken);
        if (job.StorageKind == StorageKind.RemoteGateway) await (await ResolveAsync(job, cancellationToken)).PrepareAsync(job, cancellationToken);
    }

    private async Task<IBackupStorage> ResolveAsync(BackupJob job, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (job.StorageKind is StorageKind.LocalDirectory or StorageKind.NetworkShare)
        {
            if (job.NetworkShare is null) return files;
            if (job.StorageKind != StorageKind.NetworkShare || job.Validate().Count != 0)
                throw new InvalidDataException("Некорректная привязка сетевого входа.");
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Отдельный вход в сетевую папку доступен в Windows.");
            var networkLogin = await new WindowsNetworkShareCredentialStore().GetAsync(job.NetworkShare.CredentialKey, cancellationToken);
            return new WindowsNetworkShareStorage(files, networkLogin, job.NetworkShare.ShareRoot);
        }
        if (job.StorageKind == StorageKind.RemoteGateway)
        {
            if (job.RemoteGateway is null || job.Validate().Count != 0) throw new InvalidDataException("Некорректное подключение шлюза.");
            var pairing = remoteConnections is not null ? await remoteConnections(job.RemoteGateway, cancellationToken)
                : OperatingSystem.IsWindows() ? await new WindowsRemoteGatewayConnections(Path.GetDirectoryName(Path.GetFullPath(verificationDirectory))!).GetAsync(job.RemoteGateway, cancellationToken)
                : throw new PlatformNotSupportedException("Импорт подключения основного приложения доступен в Windows.");
            var local = Uri.TryCreate(pairing.Endpoint, UriKind.Absolute, out var address) && address.Scheme == "ws" && address.IsLoopback;
            return new RemoteGatewayBackupStorage(new SPBackup.Remote.RemoteStorageClient(pairing, allowLocalEndpoint: local));
        }
        if (job.StorageKind == StorageKind.GoogleDrive)
        {
            if (job.GoogleDrive is null || job.Validate().Count != 0) throw new InvalidDataException("Некорректное хранилище Google Drive.");
            var store = googleCredentials ?? (OperatingSystem.IsWindows() ? new WindowsGoogleDriveCredentialStore()
                : throw new PlatformNotSupportedException("Подключение Google Drive доступно в Windows."));
            var value = GoogleDriveCredentials.Parse(await store.GetAsync(job.GoogleDrive.CredentialKey, cancellationToken));
            return googleFactory?.Invoke(value) ?? new GoogleDriveBackupStorage(new GoogleDriveClient(googleHttp, value));
        }
        if (job.StorageKind != StorageKind.Ftp || job.Ftp is null || job.Validate().Count != 0)
            throw new InvalidDataException("Некорректное хранилище задания.");
        var login = await credentials.GetAsync(job.Ftp.CredentialKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(login.UserName) || login.UserName.Any(char.IsControl) ||
            login.Password.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new InvalidDataException("Некорректные учётные данные FTP: логин и пароль не должны содержать переводы строк или нулевые символы.");
        var verificationRoot = job.TemporaryDirectory is null ? verificationDirectory
            : Path.Combine(job.TemporaryDirectory, ".onec-backup-ftp-verification");
        return ftpFactory?.Invoke(login, verificationRoot) ?? new FtpBackupStorage(login, verificationRoot);
    }

    public async Task<StoredBackup> StoreAsync(string completedBackupPath, BackupJob job, CancellationToken cancellationToken = default) =>
        await (await ResolveAsync(job, cancellationToken)).StoreAsync(completedBackupPath, job, cancellationToken);
    public async Task VerifyAsync(StoredBackup backup, BackupJob job, CancellationToken cancellationToken = default) =>
        await (await ResolveAsync(job, cancellationToken)).VerifyAsync(backup, job, cancellationToken);
    public async Task CopyToAsync(StoredBackup backup, BackupJob job, Stream destination, CancellationToken cancellationToken = default) =>
        await (await ResolveAsync(job, cancellationToken)).CopyToAsync(backup, job, destination, cancellationToken);
    public async Task DeleteAsync(StoredBackup backup, BackupJob job, CancellationToken cancellationToken = default) =>
        await (await ResolveAsync(job, cancellationToken)).DeleteAsync(backup, job, cancellationToken);
}
