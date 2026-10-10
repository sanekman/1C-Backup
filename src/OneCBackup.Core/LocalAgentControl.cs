namespace OneCBackup.Core;

public enum LocalAgentPhase { Running, Stopping, Stopped, Faulted }
public sealed record LocalAgentState(Guid SessionId, int ProcessId, DateTimeOffset StartedUtc,
    DateTimeOffset UpdatedUtc, LocalAgentPhase Phase, string? Error = null, string? MonitoringError = null);
public sealed record LocalAgentStopRequest(Guid SessionId);

/// <summary>Current-user control files. Session binding prevents an old request stopping a later process.</summary>
public sealed class LocalAgentControlStore(string directory)
{
    private readonly AgentJsonFile<LocalAgentState> state = new(Path.Combine(directory, "local-agent.json"));
    private readonly AgentJsonFile<LocalAgentStopRequest> stop = new(Path.Combine(directory, "local-agent-stop.json"));
    private static void Validate(LocalAgentState value)
    {
        if (value.SessionId == Guid.Empty || value.ProcessId <= 0 || value.StartedUtc == default ||
            value.UpdatedUtc < value.StartedUtc || !Enum.IsDefined(value.Phase))
            throw new InvalidDataException("Некорректное состояние фонового агента.");
    }
    public async Task<LocalAgentState?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var value = await state.ReadAsync(cancellationToken);
        if (value is not null) Validate(value);
        return value;
    }
    public Task WriteAsync(LocalAgentState value, CancellationToken cancellationToken = default)
    {
        Validate(value);
        return state.WriteAsync(value, cancellationToken);
    }
    public Task RequestStopAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("Не указан сеанс агента.");
        return stop.WriteAsync(new LocalAgentStopRequest(sessionId), cancellationToken);
    }
    public async Task<bool> StopRequestedAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        (await stop.ReadAsync(cancellationToken))?.SessionId == sessionId;
}
