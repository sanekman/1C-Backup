namespace OneCBackup.Core;

public enum StorageKind { LocalDirectory, NetworkShare, Ftp, GoogleDrive, RemoteGateway }

// Credentials are supplied at runtime. The desktop binds this key to Windows Credential Manager.
public sealed record FtpSettings
{
    public string Host { get; init; } = "";
    public int Port { get; init; } = 21;
    public bool UseTls { get; init; } = true;
    public string CredentialKey { get; init; } = "";

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Uri.CheckHostName(Host) == UriHostNameType.Unknown)
            errors.Add("Укажите имя или IP-адрес FTP-сервера без схемы и пути.");
        if (Port is < 1 or > 65535) errors.Add("Порт FTP должен быть от 1 до 65535.");
        if (string.IsNullOrWhiteSpace(CredentialKey)) errors.Add("Укажите идентификатор учётных данных FTP.");
        return errors;
    }
}
