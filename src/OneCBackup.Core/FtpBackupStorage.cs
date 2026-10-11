using System.Net;
using System.Security.Cryptography;
using FluentFTP;

namespace OneCBackup.Core;

/// <summary>Passive FTP or explicit FTPS. Credentials are injected, never serialized.</summary>
public sealed class FtpBackupStorage(NetworkCredential credentials, string verificationDirectory) : IBackupStorage
{
    private static AsyncFtpClient CreateClient(FtpSettings settings, NetworkCredential credentials)
    {
        var client = new AsyncFtpClient(settings.Host, credentials, settings.Port);
        client.Config.EncryptionMode = settings.UseTls ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None;
        client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;
        client.Config.DataConnectionEncryption = settings.UseTls;
        client.Config.ValidateAnyCertificate = false;
        client.Config.DownloadDataType = FtpDataType.Binary;
        return client;
    }

    public Task DeleteAsync(StoredBackup backup, BackupJob job, CancellationToken cancellationToken = default) =>
        VerifyOrDeleteAsync(backup, job, delete: true, cancellationToken);

    public Task VerifyAsync(StoredBackup backup, BackupJob job, CancellationToken cancellationToken = default) =>
        VerifyOrDeleteAsync(backup, job, delete: false, cancellationToken);

    public async Task CopyToAsync(StoredBackup backup, BackupJob job, Stream destination, CancellationToken cancellationToken = default)
    {
        if (job.StorageKind != StorageKind.Ftp || job.Validate().Count != 0)
            throw new ArgumentException("Некорректное FTP-хранилище.", nameof(job));
        var remote = StorageAddress.ValidateLocation(job, backup);
        cancellationToken.ThrowIfCancellationRequested();
        using var client = CreateClient(job.Ftp!, credentials);
        await client.Connect(cancellationToken);
        if (!await client.FileExists(remote, cancellationToken)) throw new FileNotFoundException("FTP-копия не найдена.", remote);
        if (!await client.DownloadStream(destination, remote, token: cancellationToken))
            throw new IOException("Не удалось скачать FTP-копию.");
    }

    private async Task VerifyOrDeleteAsync(StoredBackup backup, BackupJob job, bool delete, CancellationToken cancellationToken)
    {
        if (job.StorageKind != StorageKind.Ftp || job.Validate().Count != 0)
            throw new ArgumentException("Некорректное FTP-хранилище.", nameof(job));
        var remote = StorageAddress.ValidateLocation(job, backup);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(verificationDirectory);
        var temporary = Path.Combine(verificationDirectory, Guid.NewGuid().ToString("N") + ".verify");
        using var client = CreateClient(job.Ftp!, credentials);
        try
        {
            await client.Connect(cancellationToken);
            if (!await client.FileExists(remote, cancellationToken))
            {
                if (delete) return;
                throw new FileNotFoundException("FTP-копия не найдена.", remote);
            }
            if (await client.DownloadFile(temporary, remote, FtpLocalExists.Skip, token: cancellationToken) != FtpStatus.Success)
                throw new IOException("Не удалось проверить FTP-копию перед удалением.");
            await StorageAddress.VerifyFileAsync(temporary, backup, cancellationToken);
            if (delete) await client.DeleteFile(remote, cancellationToken);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<StoredBackup> StoreAsync(string completedBackupPath, BackupJob job,
        CancellationToken cancellationToken = default)
    {
        if (job.StorageKind != StorageKind.Ftp)
            throw new ArgumentException("Это хранилище поддерживает только FTP.", nameof(job));
        var errors = job.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(job));
        ArgumentNullException.ThrowIfNull(credentials);
        var settings = job.Ftp!;
        cancellationToken.ThrowIfCancellationRequested();
        var source = Path.GetFullPath(completedBackupPath);
        var fileName = Path.GetFileName(source);
        if (fileName.Any(char.IsControl) || fileName.Contains('\\'))
            throw new ArgumentException("Имя копии содержит недопустимые символы.", nameof(completedBackupPath));
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = input.Length;
        var sourceHash = await SHA256.HashDataAsync(input, cancellationToken);
        var remote = job.DestinationDirectory.TrimEnd('/') + "/" + fileName;
        var temporary = remote + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(verificationDirectory);
        var verificationFile = Path.Combine(verificationDirectory, Guid.NewGuid().ToString("N") + ".verify");
        using var client = CreateClient(settings, credentials);
        var uploadStarted = false;
        try
        {
            await client.Connect(cancellationToken);
            if (await client.FileExists(remote, cancellationToken))
                throw new IOException("Копия с таким именем уже существует на FTP-сервере.");
            uploadStarted = true;
            var uploaded = await client.UploadFile(source, temporary, FtpRemoteExists.Skip, createRemoteDir: true,
                token: cancellationToken);
            if (uploaded != FtpStatus.Success) throw new IOException("Загрузка копии на FTP не завершена.");

            // A full readback works even on servers without HASH/SHA256 support.
            var downloaded = await client.DownloadFile(verificationFile, temporary, FtpLocalExists.Skip,
                token: cancellationToken);
            if (downloaded != FtpStatus.Success) throw new IOException("Не удалось прочитать копию с FTP для проверки.");
            await using (var stored = File.OpenRead(verificationFile))
            {
                var storedHash = await SHA256.HashDataAsync(stored, cancellationToken);
                if (stored.Length != length || !CryptographicOperations.FixedTimeEquals(sourceHash, storedHash))
                    throw new InvalidDataException("Проверка содержимого копии на FTP не пройдена.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!await client.MoveFile(temporary, remote, FtpRemoteExists.Skip, cancellationToken))
                throw new IOException("Не удалось сохранить FTP-копию под окончательным именем.");
            var location = new UriBuilder(settings.UseTls ? "ftps" : "ftp", settings.Host, settings.Port, remote).Uri.AbsoluteUri;
            return new StoredBackup(location, length, Convert.ToHexString(sourceHash));
        }
        catch (Exception operationError)
        {
            if (uploadStarted && client.IsConnected)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    if (await client.FileExists(temporary, cleanup.Token))
                        await client.DeleteFile(temporary, cleanup.Token);
                }
                catch (Exception cleanupError)
                {
                    throw new IOException("Операция FTP завершилась ошибкой; не удалось удалить временный файл " + temporary,
                        new AggregateException(operationError, cleanupError));
                }
            }
            throw;
        }
        finally { if (File.Exists(verificationFile)) File.Delete(verificationFile); }
    }
}
