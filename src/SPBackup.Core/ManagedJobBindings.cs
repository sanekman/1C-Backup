using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SPBackup.Core;

public sealed record ManagedJobAccess(Guid JobId, string Scope, string? ArchivePasswordKey, IReadOnlyDictionary<int, string> NetworkKeys);

/// <summary>Local enrollment-specific references, never part of server configurations or exports.</summary>
public sealed class ManagedJobBindings(string directory)
{
    private string PathFor(Guid id) => Path.Combine(directory, id.ToString("N") + ".json");
    public static string Scope(BackupJob job) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        job.Id, job.Kind, job.Source, job.ManagedSecrets,
        Destinations = job.StorageJobs.Select(j => new { j.StorageKind, j.DestinationDirectory }).ToArray()
    }))));
    private static bool Key(string? value) => value is { Length: > 0 and <= 512 } && !value.Any(char.IsControl);
    public async Task SaveAsync(BackupJob job, ManagedJobAccess access, CancellationToken token = default)
    {
        var requirements = job.ManagedSecrets ?? throw new InvalidDataException("Задание не запрашивает защищённый доступ.");
        if (access.JobId != job.Id || access.Scope != Scope(job) || requirements.ArchivePassword != (access.ArchivePasswordKey is not null)
            || access.ArchivePasswordKey is not null && !Key(access.ArchivePasswordKey)
            || access.NetworkKeys is null || access.NetworkKeys.Count != requirements.NetworkDestinations.Count
            || requirements.NetworkDestinations.Any(i => !access.NetworkKeys.TryGetValue(i, out var key) || !Key(key)))
            throw new InvalidDataException("Защищённый доступ не соответствует источнику и хранилищам задания.");
        await new AgentJsonFile<ManagedJobAccess>(PathFor(job.Id)).WriteAsync(access, token);
    }
    public async Task<BackupJob> ResolveAsync(BackupJob job, CancellationToken token = default)
    {
        if (job.ManagedSecrets is null) return job;
        // Fail closed even if a caller supplies an already resolved snapshot after changing its address.
        job = job with { ArchivePasswordKey = null, NetworkShare = null,
            AdditionalDestinations = job.AdditionalDestinations?.Select(d => d with { NetworkShare = null }).ToArray() };
        ManagedJobAccess? access;
        try { access = await new AgentJsonFile<ManagedJobAccess>(PathFor(job.Id)).ReadBoundedAsync(16384, token); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { return job; }
        if (access is null || access.Scope != Scope(job) || access.JobId != job.Id) return job;
        var requirements = job.ManagedSecrets;
        if (requirements.ArchivePassword && !Key(access.ArchivePasswordKey) || access.NetworkKeys is null
            || requirements.NetworkDestinations.Any(i => !access.NetworkKeys.TryGetValue(i, out var key) || !Key(key))) return job;
        var resolved = job with { ArchivePasswordKey = requirements.ArchivePassword ? access.ArchivePasswordKey : null };
        NetworkShareSettings? Settings(int index, string path) => requirements.NetworkDestinations.Contains(index)
            ? new() { ShareRoot = NetworkShareSettings.Root(path), CredentialKey = access.NetworkKeys[index] } : null;
        return resolved with { NetworkShare = Settings(0, job.DestinationDirectory), AdditionalDestinations = job.AdditionalDestinations?
            .Select((d, index) => d with { NetworkShare = Settings(index + 1, d.DestinationDirectory) }).ToArray() };
    }
    public static bool Ready(BackupJob job) => job.ManagedSecrets is not { } required
        || (!required.ArchivePassword || Key(job.ArchivePasswordKey))
            && required.NetworkDestinations.All(i => job.StorageJobs.ElementAtOrDefault(i)?.NetworkShare is { } network && Key(network.CredentialKey));
}
