namespace SPBackup.Core;

public sealed record ScheduledExecution(Guid JobId, DateTimeOffset ScheduledAt, Exception? Error);

/// <summary>A tick-driven dispatcher. Its host supplies the actual backup executor.</summary>
public sealed class BackupScheduler(StartupBackupSchedule? startup = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<Guid, (string Settings, DateTimeOffset Next)> pending = [];

    public async Task RunAsync(Func<CancellationToken, Task<IReadOnlyList<BackupJob>>> loadJobs,
        Func<BackupJob, CancellationToken, Task> execute,
        Func<IReadOnlyList<ScheduledExecution>, Task> report,
        TimeSpan? pollInterval = null, TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default, Func<Exception?, Task>? reportWarning = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        using var timer = new PeriodicTimer(pollInterval ?? TimeSpan.FromSeconds(30), clock);
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var jobs = await loadJobs(cancellationToken);
                var results = await RunDueAsync(jobs, clock.GetUtcNow(), execute, cancellationToken);
                if (results.Count != 0) await report(results);
                if (reportWarning is not null) await reportWarning(null);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested && reportWarning is not null && AgentRuntimeWarnings.Recoverable(error))
            { await reportWarning(error); }
        } while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<ScheduledExecution>> RunDueAsync(IReadOnlyList<BackupJob> jobs,
        DateTimeOffset now, Func<BackupJob, CancellationToken, Task> execute,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execute);
        if (jobs.Select(j => j.Id).Distinct().Count() != jobs.Count)
            throw new ArgumentException("Идентификаторы заданий должны быть уникальными.", nameof(jobs));
        foreach (var job in jobs)
            if (job.ValidateSchedule().Count != 0) throw new ArgumentException("Некорректное расписание.", nameof(jobs));
        await gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var id in pending.Keys.Except(jobs.Select(j => j.Id)).ToArray()) pending.Remove(id);
            var results = new List<ScheduledExecution>();
            foreach (var job in jobs)
            {
                if (!job.Enabled || job.ScheduleKind == ScheduleKind.Manual) { pending.Remove(job.Id); continue; }
                if (job.ScheduleKind == ScheduleKind.SystemStartup)
                {
                    pending.Remove(job.Id);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (startup is null) throw new InvalidOperationException("Не задана история запусков при загрузке системы.");
                    Exception? startupError = null;
                    try
                    {
                        if (!await startup.TryConsumeAsync(job.Id, cancellationToken)) continue;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) when (AgentRuntimeWarnings.Recoverable(ex)) { startupError = ex; }
                    if (startupError is null)
                    {
                        try { await execute(job, cancellationToken); }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception ex) { startupError = ex; }
                    }
                    results.Add(new ScheduledExecution(job.Id, now, startupError));
                    continue;
                }
                var effectiveZone = BackupSchedule.ResolveTimeZone(job.TimeZoneId);
                var zoneIdentity = job.TimeZoneId == BackupSchedule.LocalTimeZone ? effectiveZone.ToSerializedString() : job.TimeZoneId;
                var settings = $"{job.ScheduleKind}|{(int)job.DaysOfWeek}|{string.Join(',', job.ScheduledTimes.Select(t => t.Ticks))}|{zoneIdentity}";
                if (!pending.TryGetValue(job.Id, out var state) || state.Settings != settings)
                    state = (settings, BackupSchedule.NextRun(job, now.AddTicks(-1))!.Value);
                if (state.Next > now) { pending[job.Id] = state; continue; }
                // Consume the slot before execution. Failures wait for the next slot.
                pending[job.Id] = (settings, BackupSchedule.NextRun(job, now)!.Value);
                cancellationToken.ThrowIfCancellationRequested();
                Exception? error = null;
                try { await execute(job, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { error = ex; }
                results.Add(new ScheduledExecution(job.Id, state.Next, error));
            }
            return results;
        }
        finally { gate.Release(); }
    }
}
