using SPBackup.Core;

namespace SPBackup.Desktop;

internal static class FtpAccessDraft
{
    public static string? KeyFor(FtpSettings? access, string host, int port, bool useTls) =>
        access is not null && string.Equals(access.Host.Trim(), host.Trim(), StringComparison.OrdinalIgnoreCase)
            && access.Port == port && access.UseTls == useTls && !string.IsNullOrWhiteSpace(access.CredentialKey)
            ? access.CredentialKey : null;

    public static string? ValidateAddress(string host, string portText, out int port)
    {
        if (!int.TryParse(portText, out port) || port is < 1 or > 65535)
            return "Укажите порт FTP от 1 до 65535.";
        return Uri.CheckHostName(host.Trim()) == UriHostNameType.Unknown
            ? "Укажите имя или IP-адрес FTP-сервера без схемы и пути." : null;
    }

    public static string AddressLabel(string host, int port, bool useTls)
    {
        host = host.Trim();
        if (Uri.CheckHostName(host) == UriHostNameType.IPv6 && !host.StartsWith('['))
            host = "[" + host + "]";
        return $"{(useTls ? "FTPS" : "FTP")} · {host}:{port}";
    }

    public static string Status(FtpSettings? access, string? key, bool edited) =>
        key is not null
            ? edited ? "Доступ задан в черновике. Применится после сохранения задания."
                : "Доступ задан. Проверьте хранилище под фактическим исполнителем."
            : access is null || string.IsNullOrWhiteSpace(access.CredentialKey)
                ? "Доступ к хранилищу не задан."
                : "Адрес, порт или режим защиты изменился. Задайте доступ для нового подключения.";
}
