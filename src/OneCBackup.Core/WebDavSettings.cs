using System.Net;
using System.Runtime.Versioning;

namespace OneCBackup.Core;

public sealed record WebDavSettings
{
    public string CredentialKey { get; init; } = "";
    public int TimeoutMinutes { get; init; } = 120;
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(CredentialKey) || CredentialKey.Length > 512 || CredentialKey.Any(char.IsControl))
            errors.Add("Сохраните логин и пароль приложения WebDAV.");
        if (TimeoutMinutes is < 1 or > 1440) errors.Add("Время ожидания WebDAV: от 1 до 1440 минут.");
        return errors;
    }
    public static IReadOnlyList<string> ValidateSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 4096 || source.Any(char.IsControl)
            || !Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return ["Укажите HTTPS-адрес папки WebDAV без логина, пароля, параметров и фрагмента."];
        return [];
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsWebDavCredentialStore : IFtpCredentialStore
{
    private readonly WindowsFtpCredentialStore store = new("WebDAV");
    public async Task<NetworkCredential> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        try { return await store.GetAsync(key, cancellationToken); }
        catch (InvalidOperationException) { throw new InvalidOperationException("Учётные данные WebDAV недоступны. Сохраните логин и пароль приложения."); }
    }
    public bool Delete(string key) => store.Delete(key);
    public void Save(string key, string username, string password)
    {
        if (username.Contains(':')) throw new ArgumentException("Логин WebDAV не должен содержать двоеточие.");
        store.Save(key, username, password);
    }
}
