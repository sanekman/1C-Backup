using System.Net;
using System.Runtime.Versioning;

namespace OneCBackup.Core;

[SupportedOSPlatform("windows")]
public sealed class WindowsNotificationCredentialStore : IFtpCredentialStore
{
    private readonly WindowsFtpCredentialStore store = new("Notifications");
    public Task<NetworkCredential> GetAsync(string key, CancellationToken cancellationToken = default) => store.GetAsync(key, cancellationToken);
    public void Save(string key, string token) => store.Save(key, "bot", token);
    public async Task ExportForServiceAsync(string directory)
    {
        foreach (var channel in (await new NotificationSettingsStore(directory).LoadAsync()).Channels.Where(c => c.Enabled))
            await store.ExportForServiceAsync(channel.CredentialKey);
    }
}
