using System.Security.Cryptography;

namespace SPBackup.Core;

internal static class StorageAddress
{
    public static string Identity(BackupJob job)
    {
        if (job.StorageKind == StorageKind.GoogleDrive) return "GoogleDrive:" + job.DestinationDirectory;
        if (job.StorageKind == StorageKind.RemoteGateway) return "RemoteGateway:" + job.RemoteGateway!.GatewayId;
        if (job.StorageKind == StorageKind.Ftp)
            return new UriBuilder(job.Ftp!.UseTls ? "ftps" : "ftp", job.Ftp.Host.ToLowerInvariant(), job.Ftp.Port,
                job.DestinationDirectory.TrimEnd('/') + "/").Uri.AbsoluteUri;
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(job.DestinationDirectory));
        return job.StorageKind + ":" + (OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path);
    }

    public static string ValidateLocation(BackupJob job, StoredBackup backup)
    {
        if (job.StorageKind == StorageKind.GoogleDrive) return GoogleDriveSettings.FileId(job, backup);
        if (job.StorageKind == StorageKind.RemoteGateway) return RemoteGatewaySettings.ObjectId(job, backup).ToString("N");
        if (job.StorageKind == StorageKind.Ftp)
        {
            var settings = job.Ftp!;
            if (!Uri.TryCreate(backup.Location, UriKind.Absolute, out var uri) ||
                uri.Scheme != (settings.UseTls ? "ftps" : "ftp") ||
                !string.Equals(uri.Host, settings.Host, StringComparison.OrdinalIgnoreCase) || uri.Port != settings.Port ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new InvalidDataException("Копия не принадлежит текущему FTP-хранилищу.");
            var remote = Uri.UnescapeDataString(uri.AbsolutePath);
            var prefix = job.DestinationDirectory.TrimEnd('/') + "/";
            if (!remote.StartsWith(prefix, StringComparison.Ordinal) || remote[prefix.Length..].Length == 0 ||
                remote[prefix.Length..].Contains('/') || remote[prefix.Length..].Contains('\\')
                || remote[prefix.Length..] is "." or ".." || remote.Any(char.IsControl))
                throw new InvalidDataException("Копия находится вне каталога задания.");
            return remote;
        }
        var fullPath = Path.GetFullPath(backup.Location);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(job.DestinationDirectory));
        var directory = Path.GetDirectoryName(fullPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(directory, root, comparison))
            throw new InvalidDataException("Копия находится вне каталога задания.");
        return fullPath;
    }

    public static async Task VerifyFileAsync(string path, StoredBackup backup, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(input, cancellationToken);
        if (input.Length != backup.Length || !string.Equals(Convert.ToHexString(hash), backup.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Размер или SHA256 копии не совпадает с журналом; файл изменён после сохранения.");
    }
}
