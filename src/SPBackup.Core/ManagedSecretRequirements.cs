#if SERVER_CONTRACTS
namespace SPBackup.Server;
#else
namespace SPBackup.Core;
#endif

// A requirement is public. Actual Windows Vault references are assigned only on the agent.
public sealed record ManagedSecretRequirements(bool ArchivePassword, IReadOnlyList<int> NetworkDestinations)
{
    public bool Valid(int destinations) => NetworkDestinations is { Count: <= 8 }
        && NetworkDestinations.All(i => i >= 0 && i < destinations) && NetworkDestinations.Distinct().Count() == NetworkDestinations.Count
        && (ArchivePassword || NetworkDestinations.Count > 0);
}
