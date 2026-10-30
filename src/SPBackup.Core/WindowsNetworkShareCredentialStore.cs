using System.Net;
using System.Runtime.Versioning;

namespace SPBackup.Core;

[SupportedOSPlatform("windows")]
public sealed class WindowsNetworkShareCredentialStore : IFtpCredentialStore
{
    private readonly WindowsFtpCredentialStore store = new("NetworkShare", exportNewSecrets: false);
    public async Task<NetworkCredential> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        try { return await store.GetAsync(key, cancellationToken); }
        catch (InvalidOperationException error)
        { throw new InvalidOperationException("Не удалось прочитать сетевой логин. В программе владельца откройте хранилище, задайте логин и пароль NAS и сохраните задание.", error); }
    }
    public void Save(string key, string username, string password) => store.Save(key, username, password);
    public bool Delete(string key) => store.Delete(key);
    public Task ExportForServiceAsync(string key, CancellationToken token = default) => store.ExportForServiceAsync(key, token);
}
