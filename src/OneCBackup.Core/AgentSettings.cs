using System.Text.Json.Serialization;

namespace OneCBackup.Core;

public enum AgentMode { Standalone, Managed }

public sealed record AgentSettings
{
    public Guid AgentId { get; init; } = Guid.NewGuid();
    public AgentMode Mode { get; init; } = AgentMode.Standalone;
    public string ServerAddress { get; init; } = "";
    public string CredentialKey { get; init; } = "";
    public int SyncIntervalSeconds { get; init; } = 60;

    [JsonIgnore]
    public string NormalizedServerAddress => ServerAddress.TrimEnd('/') + "/";

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (AgentId == Guid.Empty) errors.Add("Не указан идентификатор агента.");
        if (!Enum.IsDefined(Mode)) errors.Add("Неизвестный режим агента.");
        if (SyncIntervalSeconds is < 15 or > 86400) errors.Add("Интервал связи должен быть от 15 до 86400 секунд.");
        if (Mode == AgentMode.Managed)
        {
            if (!Uri.TryCreate(ServerAddress, UriKind.Absolute, out var address) || address.Scheme != "https" ||
                address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0)
                errors.Add("Укажите HTTPS-адрес API без пароля, параметров и фрагмента.");
            if (string.IsNullOrWhiteSpace(CredentialKey)) errors.Add("Укажите идентификатор учётных данных агента.");
        }
        return errors;
    }
}
