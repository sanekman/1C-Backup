#if SERVER_CONTRACTS
namespace SPBackup.Server;
#else
namespace SPBackup.Core;
#endif

/// <summary>A statement about validation and durable caching, never proof of backup execution.</summary>
public sealed record ConfigurationReceipt(Guid AgentId, long Revision, Guid OfferId, string Status, string? Reason, DateTimeOffset ObservedUtc)
{
    public const string OfferHeader = "SP-Backup-Configuration-Offer";
    public bool Valid() => AgentId != Guid.Empty && Revision > 0 && OfferId != Guid.Empty && ObservedUtc != default
        && ObservedUtc.Offset == TimeSpan.Zero && (Status == "Accepted" && Reason is null
            || Status == "Rejected" && Reason is "InvalidConfiguration" or "CacheWriteFailed");
}
