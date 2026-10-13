namespace OneCBackup.Core;

public enum LocalAgentPhase { Running, Stopping, Stopped, Faulted }
public sealed record LocalAgentState(Guid SessionId, int ProcessId, DateTimeOffset StartedUtc,
    DateTimeOffset UpdatedUtc, LocalAgentPhase Phase, string? Error = null, string? MonitoringError = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool CanCancelCurrentRun { get; init; }
}
public sealed record LocalAgentStopRequest(Guid SessionId);
public sealed record LocalAgentCancelRunRequest(Guid SessionId, Guid RunId);

/// <summary>Current-user control files. Session binding prevents an old request stopping a later process.</summary>
public sealed class LocalAgentControlStore(string directory)
{
    private readonly AgentJsonFile<LocalAgentState> state = new(Path.Combine(directory, "local-agent.json"));
    private readonly AgentJsonFile<LocalAgentStopRequest> stop = new(Path.Combine(directory, "local-agent-stop.json"));
    private readonly AgentJsonFile<LocalAgentCancelRunRequest> cancelRun = new(Path.Combine(directory, "local-agent-cancel-run.json"));
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
    public Task RequestCancelRunAsync(Guid sessionId, Guid runId, CancellationToken token = default)
    {
        if (sessionId == Guid.Empty || runId == Guid.Empty) throw new ArgumentException("Не указан сеанс агента или запуск задания.");
        return cancelRun.WriteAsync(new(sessionId, runId), token);
    }
    public async Task<LocalAgentCancelRunRequest?> ReadCancelRunAsync(CancellationToken token = default)
    {
        var request = await cancelRun.ReadAsync(token);
        if (request is not null && (request.SessionId == Guid.Empty || request.RunId == Guid.Empty))
            throw new InvalidDataException("Некорректная команда остановки задания.");
        return request;
    }
}
