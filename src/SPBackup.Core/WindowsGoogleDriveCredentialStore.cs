using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;

namespace SPBackup.Core;

[SupportedOSPlatform("windows")]
public sealed class WindowsGoogleDriveCredentialStore : IFtpCredentialStore
{
    private readonly WindowsFtpCredentialStore store = new("GoogleDrive");
    public async Task<NetworkCredential> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        try { return await store.GetAsync(key, cancellationToken); }
        catch (InvalidOperationException ex)
        { throw new InvalidOperationException("Подключение Google Drive недоступно. Подключите аккаунт в настройках хранилища.", ex); }
    }
    public void Save(string key, GoogleDriveCredentials value)
    { value.Validate(); store.Save(key, value.ClientId, JsonSerializer.Serialize(value)); }
    public bool Delete(string key) => store.Delete(key);
}
