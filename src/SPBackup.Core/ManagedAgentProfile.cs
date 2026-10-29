using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SPBackup.Core;

/// <summary>Durable provenance of a server-selected job. The connection ID contains no bearer token.</summary>
public sealed record BackupConfigurationStamp(string ConnectionId, long Revision)
{
    internal bool Valid => Revision > 0 && ConnectionId is { Length: 64 } && ConnectionId.All(Uri.IsHexDigit);
}

/// <summary>Isolate accepted settings, receipts, startup slots, catalog and file snapshots for each enrollment.</summary>
public sealed class ManagedAgentProfile
{
    private readonly string root;
    public AgentSettings Settings { get; }
    public string ConnectionId { get; }
    public string DirectoryPath { get; }
    public string CachePath => Path.Combine(DirectoryPath, "configuration.json");
    public string StartupSchedulePath => Path.Combine(DirectoryPath, "startup-schedule.json");
    public string CatalogPath => Path.Combine(DirectoryPath, "copies.json");
    public string WorkDirectory => Path.Combine(DirectoryPath, "work");

    public ManagedAgentProfile(string directory, AgentSettings settings)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Укажите полный путь профиля агента.", nameof(directory));
        root = Path.GetFullPath(directory);
        Settings = settings;
        ConnectionId = Identity(settings);
        DirectoryPath = Path.Combine(root, "managed", ConnectionId);
    }

    public ManagedAgentProfile(string directory, AgentSettings settings, ServerMonitoringSettings connection) : this(directory, settings)
    {
        connection.Validate();
        if (ConnectionId != Identity(new AgentSettings { Mode = AgentMode.Managed, AgentId = connection.AgentId,
            ServerAddress = connection.Address, CredentialKey = connection.CredentialKey }))
            throw new InvalidDataException("Настройки агента не совпадают с сохранённым подключением. Повторите подключение при остановленном агенте.");
    }

    internal static string Identity(AgentSettings settings)
    {
        if (settings.Mode != AgentMode.Managed || settings.Validate().Count != 0
            || settings.CredentialKey.Length > 512 || settings.CredentialKey.Any(char.IsControl))
            throw new ArgumentException("Требуется настроенное серверное подключение.", nameof(settings));
        var identity = JsonSerializer.Serialize(new[] { new Uri(settings.NormalizedServerAddress).AbsoluteUri,
            settings.AgentId.ToString("D"), settings.CredentialKey });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    public AgentConfiguration CreateConfiguration(IAgentApi? api = null) =>
        new(new JobStore(Path.Combine(root, "jobs.json"), useLocalTime: true), new(CachePath), api);
}
