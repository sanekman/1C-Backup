using System.Net;
using System.Runtime.Versioning;

namespace SPBackup.Core;

public interface IOneCCredentialStore
{
    Task<NetworkCredential> GetAsync(string key, CancellationToken token = default);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsOneCCredentialStore : IOneCCredentialStore
{
    private readonly WindowsFtpCredentialStore store = new("OneCInfobase", exportNewSecrets: false);
    public async Task<NetworkCredential> GetAsync(string key, CancellationToken token = default)
    {
        try { return await store.GetAsync(key, token); }
        catch (InvalidOperationException error)
        { throw new InvalidOperationException("Задайте пользователя и пароль информационной базы 1С заново в настройках задания.", error); }
    }
    public void Save(string key, string username, string password) => store.Save(key, username, password);
    public bool Delete(string key) => store.Delete(key);
    public Task ExportForServiceAsync(string key, CancellationToken token = default) => store.ExportForServiceAsync(key, token);
}
