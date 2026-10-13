using System.IO;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

public sealed record BackupCopyRow(BackupRecord Record, string JobName,
    BackupVerificationEntry? Verification = null, bool VerificationUnavailable = false)
{
    private BackupVerificationEntry? CurrentCheck => Verification is not null && Verification.Matches(Record) ? Verification : null;
    public string CheckSummary => VerificationUnavailable ? "Нет данных" : CurrentCheck is not { } check ? "Не проверена"
        : (check.Outcome switch { BackupVerificationOutcome.Verified => "Успех", BackupVerificationOutcome.Failed => "Ошибка", _ => "Отмена" })
            + " · " + check.CheckedUtc.ToLocalTime().ToString("dd.MM HH:mm");
    public string CheckColor => VerificationUnavailable ? "#92400E" : CurrentCheck?.Outcome switch
    { BackupVerificationOutcome.Verified => "#166534", BackupVerificationOutcome.Failed => "#B91C1C", _ => "#64748B" };
    public string CheckHelp => VerificationUnavailable ? "Не удалось прочитать сохранённые результаты проверок. Проверить файл можно отдельно."
        : CurrentCheck is not { } check ? "Эту копию ещё не проверяли кнопками проверки целостности."
        : $"Последняя проверка: {check.CheckedUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}\n"
            + (check.Origin == BackupVerificationOrigin.Manual ? "Проверка выбранной копии." : "Проверка последних копий задания.")
            + "\n" + (check.Outcome == BackupVerificationOutcome.Verified ? "Размер и SHA-256 совпали с каталогом."
                : check.Outcome == BackupVerificationOutcome.Cancelled ? "Проверка отменена; целостность не подтверждена." : "Проверка не пройдена; подробности: " + check.Error)
            + "\nЭто результат на указанное время. Он не подтверждает наличие файла сейчас и не заменяет пробное восстановление.";
    public string CompactStorage => (StorageCategory switch { "google" => "Google Drive", "ftp" => Location.StartsWith("ftps:", StringComparison.OrdinalIgnoreCase) ? "FTPS" : "FTP", "network" => "Сеть", _ => "Диск" }) + " · " + SizeDescription;
    public string StorageHelp => StorageDescription + " · " + SizeDescription + "\n" + DisplayLocation;
    public DateTimeOffset CompletedUtc => Record.CompletedUtc;
    public string Location => Record.Backup.Location;
    public string DisplayLocation => StorageCategory == "google" ? "Google Drive · " + (Record.Backup.OriginalFileName ?? "Копия") : Location;
    public string EncryptionDescription => Record.Backup.ArchivePasswordKey is null ? "Без пароля" : "ZIP AES-256";
    public string Sha256 => Record.Backup.Sha256;
    public string SizeDescription => Record.Backup.Length switch
    {
        >= 1024L * 1024 * 1024 => $"{Record.Backup.Length / (1024d * 1024 * 1024):N1} ГБ",
        >= 1024L * 1024 => $"{Record.Backup.Length / (1024d * 1024):N1} МБ",
        >= 1024 => $"{Record.Backup.Length / 1024d:N1} КБ",
        _ => $"{Record.Backup.Length} Б"
    };
    public string StorageCategory => Record.StorageIdentity.StartsWith("ftp", StringComparison.OrdinalIgnoreCase)
        ? "ftp" : Record.StorageIdentity.StartsWith("GoogleDrive:", StringComparison.Ordinal) ? "google" : Record.StorageIdentity.StartsWith("NetworkShare:", StringComparison.Ordinal) ? "network" : "local";
    public string StorageDescription => StorageCategory switch
    { "google" => "Google Drive", "ftp" => "FTP / FTPS", "network" => "Сетевая папка", _ => "Локальный диск" };
    public bool CanOpenDirectory => Path.IsPathFullyQualified(Location) &&
        !Location.StartsWith("ftp", StringComparison.OrdinalIgnoreCase);
}
