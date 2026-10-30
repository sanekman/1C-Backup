namespace SPBackup.Core;

/// <summary>Derive execution settings from enrollment; a person never has to copy a credential reference.</summary>
public static class AgentModeTransition
{
    public static AgentSettings Prepare(AgentSettings current, AgentMode mode, ServerMonitoringSettings? connection,
        int? syncIntervalSeconds = null)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentException("Выберите режим работы агента.", nameof(mode));
        var updated = current with { Mode = mode, SyncIntervalSeconds = syncIntervalSeconds ?? current.SyncIntervalSeconds };
        if (mode == AgentMode.Managed)
        {
            if (connection is null) throw new InvalidOperationException("Сначала нажмите «Подключить по приглашению» и подключитесь к серверу.");
            connection.Validate();
            if (connection.AgentId != current.AgentId)
                throw new InvalidDataException("Подключение сохранено для другого агента. Подключите этот компьютер по новому приглашению.");
            updated = updated with { ServerAddress = connection.Address, CredentialKey = connection.CredentialKey };
        }
        else updated = updated with { ServerAddress = "", CredentialKey = "" };
        var errors = updated.Validate();
        if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
        return updated;
    }
}
