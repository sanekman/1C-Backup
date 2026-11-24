using System.Text.Json.Serialization;

namespace SPBackup.Core;

public enum BackupVerificationOutcome { Verified, Failed, Cancelled }
public enum BackupVerificationOrigin { Manual, LatestCopies }

public sealed record BackupVerificationEntry(
    [property: JsonRequired] Guid BackupId,
    [property: JsonRequired] Guid JobId,
    [property: JsonRequired] string StorageIdentity,
    [property: JsonRequired] string Location,
    [property: JsonRequired] string Sha256,
    [property: JsonRequired] long Length,
    [property: JsonRequired] DateTimeOffset CheckedUtc,
    [property: JsonRequired] BackupVerificationOutcome Outcome,
    [property: JsonRequired] BackupVerificationOrigin Origin,
    string? Error = null)
{
    public bool Matches(BackupRecord record) => BackupId == record.Id && JobId == record.JobId
        && StorageIdentity == record.StorageIdentity && Location == record.Backup.Location
        && Length == record.Backup.Length && string.Equals(Sha256, record.Backup.Sha256, StringComparison.OrdinalIgnoreCase);

    public static BackupVerificationEntry Create(BackupRecord record, BackupVerificationOutcome outcome,
        BackupVerificationOrigin origin, DateTimeOffset checkedUtc, string? error = null) =>
        new(record.Id, record.JobId, record.StorageIdentity, record.Backup.Location, record.Backup.Sha256, record.Backup.Length, checkedUtc.ToUniversalTime(),
            outcome, origin, error is { Length: > 2048 } ? error[..2048] : error);
}

/// <summary>Derived last-check state, independent of ownership and run history. Use one instance per GUI process.</summary>
public sealed class BackupVerificationState(string path)
{
    private sealed record Document([property: JsonRequired] int Version,
        [property: JsonRequired] List<BackupVerificationEntry> Entries);
    private readonly AgentJsonFile<Document> file = new(path);
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<IReadOnlyList<BackupVerificationEntry>> ReadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { return (await ReadCoreAsync(cancellationToken)).Entries; }
        finally { gate.Release(); }
    }

    public async Task RememberAsync(IEnumerable<BackupVerificationEntry> entries, IReadOnlyList<BackupRecord> catalog,
        CancellationToken cancellationToken = default)
    {
        var updates = entries.ToList();
        Validate(new(1, updates));
        if (updates.Any(entry => !catalog.Any(entry.Matches)))
            throw new InvalidDataException("Результат проверки не соответствует зарегистрированной копии.");
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await ReadCoreAsync(cancellationToken);
            var retained = current.Entries.Where(entry => catalog.Any(entry.Matches)).ToDictionary(entry => entry.BackupId);
            // The completed attempt replaces the previous attempt even if the system clock moved backwards.
            foreach (var entry in updates) retained[entry.BackupId] = entry;
            await file.WriteAsync(new(1, retained.Values.ToList()), cancellationToken);
        }
        finally { gate.Release(); }
    }

    private async Task<Document> ReadCoreAsync(CancellationToken cancellationToken)
    {
        var document = await file.ReadAsync(cancellationToken) ?? new Document(1, []);
        Validate(document);
        return document;
    }

    private static void Validate(Document document)
    {
        if (document.Version != 1 || document.Entries is null || document.Entries.Any(entry => entry is null
            || entry.BackupId == Guid.Empty || entry.JobId == Guid.Empty || entry.Sha256 is null
            || string.IsNullOrWhiteSpace(entry.StorageIdentity) || string.IsNullOrWhiteSpace(entry.Location)
            || entry.Sha256.Length != 64 || entry.Sha256.Any(c => !Uri.IsHexDigit(c)) || entry.Length < 0
            || entry.CheckedUtc == default || !Enum.IsDefined(entry.Outcome) || !Enum.IsDefined(entry.Origin)
            || entry.Error is { Length: > 2048 })
            || document.Entries.Select(entry => entry.BackupId).Distinct().Count() != document.Entries.Count)
            throw new InvalidDataException("Состояние проверок содержит некорректные или повторяющиеся записи.");
    }
}
