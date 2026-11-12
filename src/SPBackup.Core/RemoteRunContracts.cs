#if SERVER_CONTRACTS
namespace SPBackup.Server;
#else
namespace SPBackup.Core;
#endif

// Commands contain saved job IDs and revision numbers only. No paths, scripts or credentials.
public sealed record RemoteRunCommand(Guid CommandId, Guid JobId, long Revision, DateTimeOffset ExpiresUtc);
public sealed record RemoteRunRequest(Guid CommandId, Guid JobId, long Revision, int LifetimeMinutes = 10);
public sealed record RemoteRunReceipt(Guid CommandId, string Status, Guid? RunId = null, string? Reason = null)
{
    public bool Valid() => CommandId != Guid.Empty && Status is "Accepted" or "Running" or "Succeeded" or "Failed" or "Rejected" or "Expired" or "Interrupted"
        && (RunId is null || RunId != Guid.Empty)
        && (Status is not ("Succeeded" or "Failed") || RunId is not null)
        && (Reason is null or "JobUnavailable" or "RevisionChanged" or "AgentRestarted" or "ExecutionFailed" or "DeadlineElapsed")
        && (Status is not ("Accepted" or "Running" or "Succeeded") || Reason is null);
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Terminal => Status is "Succeeded" or "Failed" or "Rejected" or "Expired" or "Interrupted";
}
public sealed record RemoteRunState(RemoteRunCommand Command, string Status, Guid? RunId, string? Reason,
    DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc);
