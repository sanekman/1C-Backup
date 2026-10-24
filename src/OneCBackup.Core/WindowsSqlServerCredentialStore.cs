using System.Net;
using System.Runtime.Versioning;

namespace OneCBackup.Core;

public interface ISqlServerCredentialStore
{
    Task<NetworkCredential> GetAsync(string key, CancellationToken token = default);
}

/// <summary>Separate Windows Vault namespace; service access uses the existing protected DPAPI export.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSqlServerCredentialStore : ISqlServerCredentialStore
{
    private readonly WindowsFtpCredentialStore store = new("SqlServer", exportNewSecrets: false);
    public Task<NetworkCredential> GetAsync(string key, CancellationToken token = default) => store.GetAsync(key, token);
    public void Save(string key, string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > 128 || username.Any(char.IsControl))
            throw new ArgumentException("Укажите логин MS SQL длиной от 1 до 128 символов без управляющих символов.");
        if (password.Length > 128) throw new ArgumentException("Пароль MS SQL должен содержать не более 128 символов.");
        store.Save(key, username, password);
    }
    public Task ExportForServiceAsync(string key, CancellationToken token = default) => store.ExportForServiceAsync(key, token);
    public async Task ExportMonitorsForServiceAsync(IReadOnlyList<ExternalBackupMonitor> monitors, CancellationToken token = default)
    {
        foreach (var key in monitors.Where(m => m.Kind == ExternalBackupSourceKind.SqlServerHistory
            && m.SqlAuthentication == SqlServerAuthentication.SqlServer).Select(m => m.SqlCredentialKey)
            .OfType<string>().Distinct(StringComparer.Ordinal))
            await ExportForServiceAsync(key, token);
    }
    public bool Delete(string key) => store.Delete(key);
}
