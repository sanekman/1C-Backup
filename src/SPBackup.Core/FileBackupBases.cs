using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.Zip;

namespace SPBackup.Core;

internal static class FileBackupBases
{
    internal static async Task<FileBackupManifest?> ReadAsync(BackupJob job, string sourceIdentity, BackupCatalog catalog,
        FileBackupSnapshotStore snapshots, CancellationToken token, TimeProvider? timeProvider = null, IArchivePasswordStore? passwords = null)
    {
        if (job.FileBackupMode != FileBackupMode.Incremental) return null;
        FileBackupManifest? previous;
        try { previous = await snapshots.ReadAsync(job.Id, token); }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException) { previous = null; }
        var records = await catalog.ReadAsync(token);
        var tips = new List<(BackupJob Target, BackupRecord Record, IReadOnlyList<BackupRecord> Chain)>();
        FileBackupSnapshot? latest = null;
        foreach (var target in job.StorageJobs)
        {
            // Registration order survives equal timestamps and a clock correction after reboot.
            var newest = records.LastOrDefault(record => record.JobId == job.Id && record.StorageIdentity == StorageAddress.Identity(target));
            if (newest?.Backup.FileSnapshot is not { } snapshot || snapshot.SourceIdentity != sourceIdentity
                || newest.Backup.ArchivePasswordKey != job.ArchivePasswordKey || latest is not null && snapshot != latest) return null;
            try { tips.Add((target, newest, FileBackupChains.Resolve(newest, records))); }
            catch (InvalidDataException) { return null; }
            latest = snapshot;
        }
        if (latest is null) return null;
        if (latest.Depth + 1 >= (job.FullBackupDayOfWeek is null ? job.FullBackupEvery : 365)) return null;
        if (job.FullBackupDayOfWeek is { } fullDay)
        {
            var zone = BackupSchedule.ResolveTimeZone(job.TimeZoneId);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime((timeProvider ?? TimeProvider.System).GetUtcNow(), zone).DateTime);
            var due = today.AddDays(-((7 + (int)today.DayOfWeek - (int)fullDay) % 7));
            var full = records.Where(record => record.JobId == job.Id
                && record.Backup.FileSnapshot?.SnapshotId == latest.FullSnapshotId).MaxBy(record => record.CompletedUtc);
            if (full is null || DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(full.CompletedUtc, zone).DateTime) < due) return null;
        }
        // A completed and registered archive can be newer than the cache if the process died before saving it.
        if (previous?.GetSnapshot() != latest)
            previous = await RecoverAsync(job, tips, snapshots, passwords, token);
        return previous;
    }

    private static async Task<FileBackupManifest?> RecoverAsync(BackupJob job,
        IReadOnlyList<(BackupJob Target, BackupRecord Record, IReadOnlyList<BackupRecord> Chain)> tips,
        FileBackupSnapshotStore snapshots, IArchivePasswordStore? passwords, CancellationToken token)
    {
        var local = tips.Where(tip => tip.Target.StorageKind is StorageKind.LocalDirectory or StorageKind.NetworkShare).ToArray();
        if (local.Length == 0) return null; // Remote-only archives require their own download/verification mechanism.
        FileBackupManifest? recovered = null;
        try
        {
            var password = await ArchivePasswords.ResolveAsync(job.ArchivePasswordKey, passwords, token);
            foreach (var tip in local)
            foreach (var record in tip.Chain)
            {
                token.ThrowIfCancellationRequested();
                var path = BackupCopyVerifier.ValidateRecord(record, tip.Target);
                FileBackupPaths.RejectReparse(path);
                // One read handle prevents Windows writers from swapping an archive between hash and manifest reads.
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                BackupProgressContext.Report(BackupStage.Verifying);
                var hash = await SHA256.HashDataAsync(input, token);
                if (input.Length != record.Backup.Length || !Convert.ToHexString(hash).Equals(record.Backup.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Архив цепочки изменён после регистрации.");
                input.Position = 0;
                using var archive = new ZipFile(input) { IsStreamOwner = false, Password = password };
                var manifest = await FileBackupZip.ReadManifestAsync(archive, password, token);
                if (manifest.GetSnapshot() != record.Backup.FileSnapshot)
                    throw new InvalidDataException("Индекс архива не совпадает с зарегистрированной цепочкой.");
                if (record.Id == tip.Record.Id) recovered = manifest;
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException
            or CryptographicException or ZipException or InvalidOperationException)
        {
            DiagnosticLog.Current.Write(DiagnosticEvent.SnapshotRecoveryFailed, DiagnosticLevel.Warning, error);
            return null;
        }
        if (recovered is not null)
        {
            // A temporarily read-only profile must not invalidate a verified in-memory basis.
            try { await snapshots.SaveAsync(job.Id, recovered, token); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { DiagnosticLog.Current.Write(DiagnosticEvent.SnapshotCacheFailed, DiagnosticLevel.Warning, error); }
            DiagnosticLog.Current.Write(DiagnosticEvent.SnapshotCacheRecovered, values: new(Count: local.Sum(tip => tip.Chain.Count)));
        }
        return recovered;
    }
}
