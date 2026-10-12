using System.Runtime.Versioning;

namespace OneCBackup.Core;

[SupportedOSPlatform("windows")]
public sealed class WindowsUpdateCredentialStore
{
    private readonly WindowsFtpCredentialStore store = new("Updates");
    private const string Key = "GitHub";
    public async Task<string> GetAsync(CancellationToken token = default)
    {
        try { return (await store.GetAsync(Key, token)).Password; }
        catch (InvalidOperationException) { throw new InvalidOperationException("Токен GitHub не найден в Windows. Сохраните его заново."); }
    }
    public void Save(string secret)
    {
        if (secret.Length is < 1 or > 1024 || !GitHubApplicationUpdates.IsValidToken(secret)) throw new ArgumentException("Введите токен GitHub без пробелов и переводов строк.");
        store.Save(Key, "github", secret);
    }
    public void Delete() => store.Delete(Key);
}
