using System.IO;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

public sealed record BackupCopyRow(BackupRecord Record, string JobName)
{
    public DateTimeOffset CompletedUtc => Record.CompletedUtc;
    public string Location => Record.Backup.Location;
    public string Sha256 => Record.Backup.Sha256;
    public string SizeDescription => Record.Backup.Length switch
    {
        >= 1024L * 1024 * 1024 => $"{Record.Backup.Length / (1024d * 1024 * 1024):N1} ГБ",
        >= 1024L * 1024 => $"{Record.Backup.Length / (1024d * 1024):N1} МБ",
        >= 1024 => $"{Record.Backup.Length / 1024d:N1} КБ",
        _ => $"{Record.Backup.Length} Б"
    };
    public string StorageDescription => Record.StorageIdentity.StartsWith("ftp", StringComparison.OrdinalIgnoreCase)
        ? "FTP / FTPS" : Record.StorageIdentity.StartsWith("NetworkShare:", StringComparison.Ordinal)
            ? "Сетевая папка" : "Локальный диск";
    public bool CanOpenDirectory => Path.IsPathFullyQualified(Location) &&
        !Location.StartsWith("ftp", StringComparison.OrdinalIgnoreCase);
}
