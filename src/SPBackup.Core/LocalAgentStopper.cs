namespace SPBackup.Core;

/// <summary>Requests a session-bound stop and waits for the executor's lease to be released.</summary>
public sealed class LocalAgentStopper(string directory)
{
    public async Task<LocalAgentState?> StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var control = new LocalAgentControlStore(directory);
        var coordinator = new BackupEngineCoordinator(directory);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        LocalAgentState? requested = null;
        try
        {
            while (coordinator.IsHeldElsewhere())
            {
                deadline.Token.ThrowIfCancellationRequested();
                var state = await control.ReadAsync(deadline.Token);
                if (requested is not null && state is not null && state.SessionId != requested.SessionId)
                    throw new InvalidOperationException("Во время остановки запущен другой сеанс агента. Проверьте его состояние и повторите остановку.");
                if (requested is null && state?.Phase is LocalAgentPhase.Running or LocalAgentPhase.Stopping)
                {
                    requested = state;
                    await control.RequestStopAsync(state.SessionId, deadline.Token);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200), deadline.Token);
            }
            return requested;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Исполнитель ещё не освободил рабочий профиль. Остановка не подтверждена; дождитесь завершения текущей операции и повторите попытку.");
        }
    }
}
