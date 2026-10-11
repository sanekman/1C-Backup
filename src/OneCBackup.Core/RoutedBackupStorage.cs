using System.Net;

namespace OneCBackup.Core;

public interface IFtpCredentialStore
{
    Task<NetworkCredential> GetAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>Selects storage from each immutable job. Credentials are never part of the job document.</summary>
public sealed class RoutedBackupStorage(IFtpCredentialStore credentials, string verificationDirectory,
    Func<NetworkCredential, string, IBackupStorage>? ftpFactory = null) : IBackupStorage
{
    private readonly FileBackupStorage files = new();

    public async Task PrepareAsync(BackupJob job, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var errors = job.Validate();
        if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
        if (job.StorageKind == StorageKind.Ftp) await ResolveAsync(job, cancellationToken);
    }

    private async Task<IBackupStorage> ResolveAsync(BackupJob job, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (job.StorageKind is StorageKind.LocalDirectory or StorageKind.NetworkShare) return files;
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
