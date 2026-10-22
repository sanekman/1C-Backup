using System.Net;
using System.Runtime.Versioning;

namespace OneCBackup.Core;

public interface IPostgreSqlCredentialStore
{
    Task<NetworkCredential> GetAsync(string key, CancellationToken token = default);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsPostgreSqlCredentialStore : IPostgreSqlCredentialStore
{
    private readonly WindowsFtpCredentialStore store = new("PostgreSQL", exportNewSecrets: false);
    public async Task<NetworkCredential> GetAsync(string key, CancellationToken token = default)
    {
        try { return await store.GetAsync(key, token); }
        catch (InvalidOperationException error)
        { throw new InvalidOperationException("Не удалось прочитать сохранённый пароль PostgreSQL. Откройте задание в программе владельца, задайте пароль заново и сохраните задание.", error); }
    }
    public void Save(string key, string username, string password) => store.Save(key, username, password);
    public bool Delete(string key) => store.Delete(key);
    public Task ExportForServiceAsync(string key, CancellationToken token = default) => store.ExportForServiceAsync(key, token);
}
