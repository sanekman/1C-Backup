using System.Net;
using System.Runtime.Versioning;

namespace SPBackup.Core;

public interface ISqlServerCredentialStore
{
    Task<NetworkCredential> GetAsync(string key, CancellationToken token = default);
}

/// <summary>Separate Windows Vault namespace; service access uses the existing protected DPAPI export.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSqlServerCredentialStore : ISqlServerCredentialStore
{
    private readonly WindowsFtpCredentialStore store = new("SqlServer", exportNewSecrets: false);
    public async Task<NetworkCredential> GetAsync(string key, CancellationToken token = default)
    {
        try { return await store.GetAsync(key, token); }
        catch (InvalidOperationException error)
        { throw new InvalidOperationException("Не удалось прочитать сохранённые данные входа MS SQL. Откройте задание или источник мониторинга в программе владельца, задайте логин и пароль заново и сохраните настройки.", error); }
    }
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
