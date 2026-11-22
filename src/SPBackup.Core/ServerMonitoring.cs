using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SPBackup.Core.Monitoring;

namespace SPBackup.Core;

public sealed record ServerMonitoringSettings(string Address, Guid AgentId, string CredentialKey, string CertificateSha256 = "")
{
    public void Validate()
    {
        if (!Uri.TryCreate(Address, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || AgentId == Guid.Empty || string.IsNullOrWhiteSpace(CredentialKey)
            || CredentialKey.Length > 512 || CredentialKey.Any(char.IsControl)
            || CertificateSha256 is null || CertificateSha256.Length != 0 && (CertificateSha256.Length != 64 || !CertificateSha256.All(Uri.IsHexDigit)))
            throw new InvalidDataException("Укажите HTTPS-адрес сервера и SHA-256 сертификата из его панели.");
    }
}

/// <summary>Outgoing monitoring never changes local jobs, mode or schedules. History is the durable outbox.</summary>
public sealed class ServerMonitoringClient : IDisposable
{
    private readonly HttpClient client;
    private readonly bool managedExecution;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public ServerMonitoringClient(ServerMonitoringSettings settings, HttpMessageHandler? transport = null, bool managedExecution = false)
    {
        settings.Validate();
        this.managedExecution = managedExecution;
        client = new(transport ?? ServerHttpsTransport.Create(settings.CertificateSha256))
        { BaseAddress = new(settings.Address.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(20) };
    }
    public async Task<bool> CheckReadinessAsync(CancellationToken token = default)
    {
        using var response = await client.GetAsync("health/ready", HttpCompletionOption.ResponseHeadersRead, token);
        return response.StatusCode == HttpStatusCode.OK;
    }

    public async Task<string> EnrollAsync(Guid agentId, string invitationCode, string name, CancellationToken token = default)
    {
        using var response = await client.PostAsJsonAsync("api/v1/enrollment",new { agentId,invitationCode,name },token);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<Enrollment>(token) ?? throw new InvalidDataException("Пустой ответ сервера.");
        if (result.AgentId != agentId || result.ProtocolVersion != 1 || result.Token.Length != 43) throw new InvalidDataException("Некорректный ответ регистрации.");
        return result.Token;
    }
    private sealed record Enrollment(Guid AgentId, string Token, int ProtocolVersion);
    public async Task SendAsync(ServerMonitoringSettings settings, string bearer, MonitoringSnapshot snapshot,
        IReadOnlyList<MonitoredRun> runs, CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put,$"api/v1/agents/{settings.AgentId:D}/monitoring")
        { Content = JsonContent.Create(snapshot,options:Json) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",bearer);
        request.Headers.Add(AgentCapabilities.Header, AgentCapabilities.ExecutionHeaderValue(managedExecution));
        using var response = await client.SendAsync(request,token);
        // Older servers require at least one storage per job. Keep their existing Sources view
        // and continue delivering history while the server is upgraded.
        if (response.StatusCode == HttpStatusCode.BadRequest && MonitoringValidation.Valid(snapshot)
            && snapshot.Jobs.Any(job => job.TaskKind == "Observation"))
        {
            await SendAsync(settings, bearer, snapshot with { Jobs = snapshot.Jobs.Where(job => job.TaskKind != "Observation").ToArray() }, runs, token);
            return;
        }
        response.EnsureSuccessStatusCode();
        foreach (var run in runs) await SendRunAsync(settings,bearer,run,token);
    }
    public async Task SendRunAsync(ServerMonitoringSettings settings,string bearer,MonitoredRun run,CancellationToken token=default)
    {
        using var item = new HttpRequestMessage(HttpMethod.Post,$"api/v1/agents/{settings.AgentId:D}/runs")
        { Content = JsonContent.Create(run,options:Json) };
        item.Headers.Authorization = new AuthenticationHeaderValue("Bearer",bearer);
        using var result = await client.SendAsync(item,token);
        result.EnsureSuccessStatusCode();
    }
    public void Dispose() => client.Dispose();
}

public sealed class ServerMonitoringDispatcher(string directory, IFtpCredentialStore credentials,
    AgentExecutionJobSource? managedJobs = null, ServerMonitoringSettings? fixedConnection = null)
{
    private readonly AgentJsonFile<ServerMonitoringSettings> configuration = new(Path.Combine(directory,"server-monitoring.json"));
    // One cursor per destination + enrollment: switching servers never loses history on the new server.
    private sealed record Receipts(string Address, Guid AgentId, string CredentialKey, HashSet<Guid> Runs,
        Dictionary<Guid, DateTimeOffset>? RetryAfter = null, Dictionary<Guid, HttpStatusCode>? RejectionCodes = null);
    private readonly AgentJsonFile<Receipts> receipts = new(Path.Combine(directory,"server-monitoring-receipts.json"));
    public static async Task<(IReadOnlySet<Guid> Sent, IReadOnlySet<Guid> Rejected)?> ReadDeliveryAsync(
        string directory, ServerMonitoringSettings settings, CancellationToken token = default)
    {
        var file = new AgentJsonFile<Receipts>(Path.Combine(directory, "server-monitoring-receipts.json"));
        var delivery = await file.ReadAsync(token);
        if (delivery is null || delivery.Address != settings.Address || delivery.AgentId != settings.AgentId
            || delivery.CredentialKey != settings.CredentialKey) return null;
        return (delivery.Runs, delivery.RetryAfter?.Keys.ToHashSet() ?? []);
    }
    public async Task RunAsync(Action<Exception?> warning, CancellationToken token=default, TimeSpan? interval=null)
    {
        using var timer=new PeriodicTimer(interval ?? TimeSpan.FromSeconds(15));
        do
        {
            try { await PollAsync(token); warning(null); }
            catch(OperationCanceledException) when(token.IsCancellationRequested) { throw; }
            catch(Exception error) { warning(error); }
        } while(await timer.WaitForNextTickAsync(token));
    }
    public async Task PollAsync(CancellationToken token = default)
    {
        var settings = fixedConnection ?? await configuration.ReadAsync(token);
        if (settings is null) return;
        settings.Validate();
        var history = await new BackupRunHistory(Path.Combine(directory,"runs.json")).ReadAsync(token);
        var connectionId = ManagedAgentProfile.Identity(new AgentSettings { Mode = AgentMode.Managed,
            AgentId = settings.AgentId, ServerAddress = settings.Address, CredentialKey = settings.CredentialKey });
        history = history.Where(r => r.Configuration is null || r.Configuration.ConnectionId == connectionId).ToArray();
        var effective = managedJobs;
        if (effective is null && await new AgentJsonFile<AgentSettings>(Path.Combine(directory, "agent.json")).ReadAsync(token) is { Mode: AgentMode.Managed } agentSettings)
        {
            var profile = new ManagedAgentProfile(directory, agentSettings, settings);
            effective = new(profile.CreateConfiguration(), agentSettings);
        }
        if (effective is not null && effective.ConnectionId != connectionId)
            throw new InvalidDataException("Источник заданий не относится к подключению мониторинга.");
        var selected = effective is null ? null : await effective.LoadSnapshotAsync(token);
        var jobs = selected?.Jobs ?? await new JobStore(Path.Combine(directory,"jobs.json"),useLocalTime:true).LoadAsync(token);
        Dictionary<Guid, IReadOnlyList<MonitoredFileCopyCheck>> fileCopyChecks = [];
        try
        {
            if (jobs.Any(job => job.Kind == DatabaseKind.FilesAndFolders))
                fileCopyChecks = await MonitoredFileCopyChecks.ReadAsync(directory,
                    effective?.CatalogPath(directory) ?? Path.Combine(directory, "copies.json"), jobs, token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // A damaged or inaccessible local check log must not prevent delivery of run history.
            DiagnosticLog.Current.Write(DiagnosticEvent.MonitoringFailed, DiagnosticLevel.Warning, error);
        }
        var acknowledged = await receipts.ReadAsync(token);
        if (acknowledged is null || acknowledged.Address != settings.Address || acknowledged.AgentId != settings.AgentId || acknowledged.CredentialKey != settings.CredentialKey)
            acknowledged = new(settings.Address,settings.AgentId,settings.CredentialKey,[]);
        var monitors = await new ExternalBackupMonitorStore(Path.Combine(directory,"external-monitors.json")).LoadAsync(token);
        var external = await new ExternalBackupOutbox(Path.Combine(directory,"external-backups.json")).ReadAsync(token);
        var polling = await new ExternalBackupPollingStatusStore(directory).ReadAsync(token);
        var reports = new List<MonitoredRun>();
        var collectionWarnings=0;
        foreach (var run in history.Where(r=>r.FinishedUtc is not null))
        {
            var report=ToReport(run);
            if (MonitoringValidation.Valid(report)) reports.Add(report); else collectionWarnings++;
        }
        foreach (var item in external)
        {
            try
            {
                var report=ToReport(item,monitors);
                if (MonitoringValidation.Valid(report)) reports.Add(report); else collectionWarnings++;
            }
            catch (Exception e) when (e is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException) { collectionWarnings++; }
        }
        var pending = reports.Where(r=>!acknowledged.Runs.Contains(r.RunId)).OrderByDescending(r=>r.FinishedUtc).ToArray();
        var retryAfter = acknowledged.RetryAfter ?? [];
        var rejectionCodes = acknowledged.RejectionCodes ?? [];
        acknowledged = acknowledged with { RetryAfter = retryAfter, RejectionCodes = rejectionCodes };
        var pendingIds = pending.Select(run => run.RunId).ToHashSet();
        var staleRetries = retryAfter.Keys.Where(id => !pendingIds.Contains(id)).ToArray();
        foreach (var id in staleRetries) retryAfter.Remove(id);
        var staleCodes = rejectionCodes.Keys.Where(id => !retryAfter.ContainsKey(id)).ToArray();
        foreach (var id in staleCodes) rejectionCodes.Remove(id);
        var invalid = rejectionCodes.Values.Count(code => code == HttpStatusCode.BadRequest);
        var conflicting = rejectionCodes.Values.Count(code => code == HttpStatusCode.Conflict);
        var progress = await new BackupProgressStore(directory).ReadAsync();
        var observationTasks = ExternalBackupTask.Create(monitors, external, polling, settings.AgentId).ToDictionary(task => task.Id);
        var snapshot = new MonitoringSnapshot(DateTimeOffset.UtcNow, typeof(ServerMonitoringDispatcher).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute),false).Cast<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown",
            jobs.Select(j=>new MonitoredJob(j.Id,Label(j.Name),j.Kind.ToString(),j.Enabled,j.ScheduleDescription+" · "+j.TimeZoneId+(j.ScheduleKind==ScheduleKind.Weekly ? " · "+j.DaysOfWeek : ""),
                BackupSchedule.NextRun(j,DateTimeOffset.UtcNow),j.StorageJobs.Count)
                {
                    BackupScope = j.ExecutionConfiguration?.JobScope, MaximumBackupAgeHours = j.MaximumBackupAgeHours,
                    LocalAccessReady = j.ManagedSecrets is null ? null : ManagedJobBindings.Ready(j),
                    FileCopyChecks = fileCopyChecks.GetValueOrDefault(j.Id)
                })
                .Concat(monitors.Select(m=>new MonitoredJob(m.Id,Label(m.Name),m.Kind.ToString(),m.Enabled,
                    "Опрос каждые "+m.PollIntervalSeconds+" с",null,0) { TaskKind = "Observation" })).ToArray(),
            progress is { Finished:false } && history.Any(r=>r.RunId==progress.RunId && r.Status==BackupRunStatus.Running) ? new(progress.RunId,progress.JobId,
                progress.Progress.Stage == BackupStage.RetryingStorage ? "ConnectingStorage" : progress.Progress.Stage.ToString(),progress.Progress.Percent,progress.UpdatedUtc) : null,pending.Length,
            monitors.Select(m=>new MonitoredSource(m.Id,Label(m.Name),m.Kind.ToString(),m.Enabled,observationTasks[m.Id].Poll?.PolledUtc,
                observationTasks[m.Id].Poll?.Error is not null)
                { MaximumBackupAgeHours = m.MaximumBackupAgeHours, LastSuccessfulUtc = observationTasks[m.Id].LastSuccessfulUtc,
                    FreshnessWarning = observationTasks[m.Id].FreshnessWarning,
                    ObservationStatus = observationTasks[m.Id].ResultCode, LatestReportId = observationTasks[m.Id].Latest?.ReportId, LatestFinishedUtc = observationTasks[m.Id].LatestFinishedUtc,
                    PollErrorCode = observationTasks[m.Id].Poll?.Error is not null ? observationTasks[m.Id].Poll?.ErrorCode : null,
                    SqlBackupTypesReported = m.Kind == ExternalBackupSourceKind.SqlServerHistory,
                    LastFullUtc = observationTasks[m.Id].LastFullUtc, LastDifferentialUtc = observationTasks[m.Id].LastDifferentialUtc,
                    LastLogUtc = observationTasks[m.Id].LastLogUtc }).ToArray(),collectionWarnings)
        { ExecutionMode = selected is null ? "Standalone" : "Managed", ConfigurationRevision = selected?.Revision,
            BackupExecutionAvailable = selected is null ? null : managedJobs is not null && fixedConnection is not null,
            ReportRejections = new(invalid, conflicting, retryAfter.Count - invalid - conflicting) };
        var now = DateTimeOffset.UtcNow;
        // Bound stored cooldowns by the retry interval if the agent's wall clock moved back.
        var maxRetryAt = now.AddMinutes(2);
        var delayedByClock = retryAfter.Where(entry => entry.Value > maxRetryAt).Select(entry => entry.Key).ToArray();
        foreach (var id in delayedByClock) retryAfter[id] = maxRetryAt;
        var batch = pending.Where(r => !retryAfter.TryGetValue(r.RunId, out var time) || time <= now).Take(25).ToArray();
        // Only the initialized host opts in. A GUI reading a managed cache does not claim execution readiness.
        using var client = new ServerMonitoringClient(settings, managedExecution: managedJobs is not null && fixedConnection is not null);
        var secret = await credentials.GetAsync(settings.CredentialKey,token);
        await client.SendAsync(settings,secret.Password,snapshot,[],token);
        var previousReceiptCount = acknowledged.Runs.Count;
        acknowledged.Runs.IntersectWith(reports.Select(r=>r.RunId));
        if (previousReceiptCount != acknowledged.Runs.Count || staleRetries.Length != 0 || staleCodes.Length != 0 || delayedByClock.Length != 0)
            await receipts.WriteAsync(acknowledged, token);
        foreach (var item in batch)
        {
            try
            {
                await client.SendRunAsync(settings,secret.Password,item,token);
                acknowledged.Runs.Add(item.RunId);
                retryAfter.Remove(item.RunId);
                rejectionCodes.Remove(item.RunId);
                await receipts.WriteAsync(acknowledged,token);
            }
            catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
            {
                retryAfter[item.RunId] = DateTimeOffset.UtcNow.AddMinutes(2);
                rejectionCodes[item.RunId] = error.StatusCode.Value;
                await receipts.WriteAsync(acknowledged, token);
            }
        }
        if (retryAfter.Count != 0 || collectionWarnings != 0)
            throw new InvalidDataException($"Сервер отклонил отчётов: {retryAfter.Count}. Не удалось подготовить локальных отчётов: {collectionWarnings}. Исходная история сохранена.");
    }
    private static string Label(string value) => new string(value.Select(c=>char.IsControl(c) ? ' ' : c).Take(256).ToArray()).Trim() is { Length: >0 } text ? text : "Без имени";
    public static MonitoredRun ToReport(ExternalBackupReport report, IReadOnlyList<ExternalBackupMonitor> monitors)
    {
        var o=report.Observation;
        var zone=TimeZoneInfo.FindSystemTimeZoneById(o.TimeZoneId);
        var start=new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(o.StartedAt,DateTimeKind.Unspecified),zone));
        var finish=new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(o.FinishedAt,DateTimeKind.Unspecified),zone));
        return new(report.ReportId,report.MonitorId,Label(monitors.FirstOrDefault(m=>m.Id==report.MonitorId)?.Name ?? "Внешняя SQL-копия"),start,finish,
            o.Outcome==ExternalBackupOutcome.Completed ? "Succeeded" : "Failed",0,false,null,
            [new(0,o.Outcome==ExternalBackupOutcome.Completed,(long)Math.Clamp(o.SizeBytes ?? 0,0,long.MaxValue),false)],
            o.Evidence is "SqlServerMsdbHistory" or "PgRestoreCatalogChecked" or "PostgreSqlScriptFailure" ? o.Evidence : "ExternalReport")
        { Issues = o.Outcome == ExternalBackupOutcome.Completed ? null : [new("ExternalBackupFailed", "Error", "Source")] };
    }
    public static MonitoredRun ToReport(BackupRun r)
    {
        var snapshot = r.Backup?.FileSnapshot ?? r.Destinations?.Select(d=>d.Record?.Backup.FileSnapshot).FirstOrDefault(s=>s is not null);
        var finished = r.FinishedUtc ?? throw new InvalidDataException("Запуск ещё выполняется.");
        var clockMovedBack = finished < r.StartedUtc;
        // Protocol v1 peers require an ordered interval. Preserve the observed time in optional metadata.
        return new(r.RunId,r.JobId,Label(r.JobName),r.StartedUtc,clockMovedBack ? r.StartedUtc : finished,r.Status.ToString(),r.Warnings?.Count ?? 0,
            snapshot?.IsIncomplete == true,snapshot is null ? null : snapshot.IsFull ? "Full" : "Incremental",r.Destinations?.Select((d,i)=>new MonitoredDestination(i,d.Record is not null,d.Record?.Backup.Length ?? 0,d.Record?.Backup.FileSnapshot?.IsIncomplete == true)).ToArray()
                ?? (r.Backup is { } backup ? [new(0,true,backup.Length,backup.FileSnapshot?.IsIncomplete == true)] : []))
        {
            ConfigurationRevision = r.Configuration?.Revision, BackupScope = r.Configuration?.JobScope,
            // Older servers accept the optional field and still validate the original issue severities.
            OpenOneCDatabase = r.Comment is not null,
            Issues = r.Comment is null ? r.Issues : r.Issues!.Where(issue => issue.Severity != "Info").ToArray(),
            Timing = r.DurationMilliseconds is not null || clockMovedBack
                ? new(r.DurationMilliseconds, clockMovedBack ? finished : null) : null
        };
    }
    public static Task SaveSettingsAsync(string directory, ServerMonitoringSettings settings, CancellationToken token = default)
    { settings.Validate(); return new AgentJsonFile<ServerMonitoringSettings>(Path.Combine(directory,"server-monitoring.json")).WriteAsync(settings,token); }
    public static async Task<ServerMonitoringSettings?> LoadSettingsAsync(string directory, CancellationToken token = default)
    {
        var settings = await new AgentJsonFile<ServerMonitoringSettings>(Path.Combine(directory, "server-monitoring.json")).ReadAsync(token);
        settings?.Validate(); return settings;
    }
}
