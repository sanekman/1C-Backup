using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SPBackup.Core;

public sealed record GoogleDriveSettings
{
    public string CredentialKey { get; init; } = "";
    public string FolderName { get; init; } = "SP Backup";
    public IReadOnlyList<string> Validate() => string.IsNullOrWhiteSpace(CredentialKey) || CredentialKey.Length > 512 || CredentialKey.Any(char.IsControl)
        ? ["Подключите аккаунт Google Drive."] : [];
    public static bool IsId(string? value) => value is not null && Regex.IsMatch(value, "^[A-Za-z0-9_-]{1,256}$");
    public static string FileId(BackupJob job, StoredBackup backup)
    {
        var prefix = "gdrive:" + job.DestinationDirectory + "/";
        if (!backup.Location.StartsWith(prefix, StringComparison.Ordinal) || !IsId(backup.Location[prefix.Length..]))
            throw new InvalidDataException("Копия не принадлежит этой папке Google Drive.");
        return backup.Location[prefix.Length..];
    }
}

// Stored only in Windows Credential Manager / the protected service secret cache.
public sealed record GoogleDriveCredentials(string ClientId, string ClientSecret, string RefreshToken)
{
    public void Validate()
    {
        if (!Regex.IsMatch(ClientId ?? "", "^[A-Za-z0-9_-]+\\.apps\\.googleusercontent\\.com$")
            || string.IsNullOrWhiteSpace(ClientSecret) || string.IsNullOrWhiteSpace(RefreshToken)
            || new[] { ClientId, ClientSecret, RefreshToken }.Any(value => value is null || value.Length > 2048 || value.Any(char.IsControl)))
            throw new InvalidDataException("Некорректное подключение Google Drive. Подключите аккаунт заново.");
    }
    public static GoogleDriveCredentials Parse(NetworkCredential value)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<GoogleDriveCredentials>(value.Password) ?? throw new InvalidDataException();
            parsed.Validate(); return parsed;
        }
        catch (JsonException) { throw new InvalidDataException("Подключение Google Drive повреждено. Подключите аккаунт заново."); }
    }
}
