using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.Zip;

namespace SPBackup.Core;

internal static class FileBackupBases
{
    private static async Task<FileStream> OpenArchiveAsync(BackupJob target, string path, CancellationToken token)
    {
        if (target.NetworkShare is not null)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Сетевой вход доступен в Windows.");
            if (target.Validate().Count != 0) throw new InvalidDataException("Проверьте привязку сетевого входа.");
            var login = await new WindowsNetworkShareCredentialStore().GetAsync(target.NetworkShare.CredentialKey, token);
            return await new WindowsNetworkShareStorage(new(), login, target.NetworkShare.ShareRoot).OpenArchiveReadAsync(path, token);
        }
        FileBackupPaths.RejectReparse(path);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }
    internal static async Task<FileBackupManifest?> ReadAsync(BackupJob job, string sourceIdentity, BackupCatalog catalog,
        FileBackupSnapshotStore snapshots, CancellationToken token, TimeProvider? timeProvider = null, IArchivePasswordStore? passwords = null, IBackupStorage? recoveryStorage = null, string? recoveryWorkRoot = null)
    {
        if (job.FileBackupMode != FileBackupMode.Incremental) return null;
        FileBackupManifest? previous;
        try { previous = await snapshots.ReadAsync(job.Id, token); }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException) { previous = null; }
        var records = await catalog.ReadAsync(token);
        var identities = job.StorageJobs.Select(StorageAddress.Identity).ToHashSet(StringComparer.Ordinal);
        var owned = records.Where(record => record.JobId == job.Id && identities.Contains(record.StorageIdentity)).ToArray();
        if (owned.Length == 0)
        {
            if (previous?.SourceIdentity == sourceIdentity)
                throw new InvalidDataException("Каталог файловой цепочки отсутствует. Восстановите каталог и доступ к архивам; незапланированная полная копия не создаётся.");
            return null;
        }
        if (owned[^1].Backup.FileSnapshot is null) return null; // A deliberate source-kind change starts a new chain.
        // A backfilled ancestor is registered later than a newer increment. Root introduction
        // order and causal depth preserve the actual chain even after a clock correction.
        var root = owned.Where(record => record.Backup.FileSnapshot is not null)
            .Select(record => record.Backup.FileSnapshot!.FullSnapshotId).Distinct().Last();
        var current = owned.Where(record => record.Backup.FileSnapshot?.FullSnapshotId == root).ToArray();
        var depth = current.Max(record => record.Backup.FileSnapshot!.Depth);
        var newest = current.Where(record => record.Backup.FileSnapshot!.Depth == depth).ToArray();
        var latest = newest[0].Backup.FileSnapshot!;
        if (latest.SourceIdentity != sourceIdentity || newest[0].Backup.ArchivePasswordKey != job.ArchivePasswordKey) return null;
        if (newest.Any(record => record.Backup.FileSnapshot != latest || record.Backup.ArchivePasswordKey != job.ArchivePasswordKey))
            throw new InvalidDataException("Обнаружены расходящиеся файловые цепочки. Проверьте каталог; незапланированная полная копия не создаётся.");
        var tips = new List<(BackupJob Target, BackupRecord Record, IReadOnlyList<BackupRecord> Chain)>();
        foreach (var record in newest)
        {
            var target = job.StorageJobs.Single(target => StorageAddress.Identity(target) == record.StorageIdentity);
            try { tips.Add((target, record, FileBackupChains.Resolve(record, records))); }
            catch (InvalidDataException) { /* Another mirror may still contain the complete registered chain. */ }
        }
        if (latest.Depth + 1 >= (job.FullBackupDayOfWeek is null ? job.FullBackupEvery : 365)) return null;
        if (job.FullBackupDayOfWeek is { } fullDay)
        {
            var zone = BackupSchedule.ResolveTimeZone(job.TimeZoneId);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime((timeProvider ?? TimeProvider.System).GetUtcNow(), zone).DateTime);
            var due = today.AddDays(-((7 + (int)today.DayOfWeek - (int)fullDay) % 7));
            var full = records.Where(record => record.JobId == job.Id
                && record.Backup.FileSnapshot?.SnapshotId == latest.FullSnapshotId).MaxBy(record => record.CompletedUtc);
            if (full is null) throw new InvalidDataException("Полная основа отсутствует в каталоге. Восстановите цепочку; внеплановая полная копия не создаётся.");
            if (DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(full.CompletedUtc, zone).DateTime) < due) return null;
        }
        // A completed and registered archive can be newer than the cache if the process died before saving it.
        if (tips.Count == 0)
            throw new InvalidDataException("Нет зарегистрированной основы инкремента. Восстановите каталог цепочки; полная копия вне плана не создаётся.");
        if (previous?.GetSnapshot() != latest)
            previous = await RecoverAsync(job, tips, snapshots, passwords, token, recoveryStorage, recoveryWorkRoot);
        return previous ?? throw new InvalidDataException("Не удалось проверить основу инкремента. Восстановите доступ к цепочке или явно выберите полную копию в параметрах задания; автоматически она не создаётся.");
    }

    private static async Task<FileBackupManifest?> RecoverAsync(BackupJob job,
        IReadOnlyList<(BackupJob Target, BackupRecord Record, IReadOnlyList<BackupRecord> Chain)> tips,
        FileBackupSnapshotStore snapshots, IArchivePasswordStore? passwords, CancellationToken token,
        IBackupStorage? recoveryStorage, string? recoveryWorkRoot)
    {
        FileBackupManifest? recovered = null;
        var password = await ArchivePasswords.ResolveAsync(job.ArchivePasswordKey, passwords, token);
        foreach (var tip in tips)
        {
            try
            {
                foreach (var record in tip.Chain)
                {
                    token.ThrowIfCancellationRequested();
                    FileBackupManifest manifest;
                    if (tip.Target.StorageKind is StorageKind.LocalDirectory or StorageKind.NetworkShare)
                    {
                        var path = BackupCopyVerifier.ValidateRecord(record, tip.Target);
                        await using var input = await OpenArchiveAsync(tip.Target, path, token);
                        BackupProgressContext.Report(BackupStage.Verifying);
                        var hash = await SHA256.HashDataAsync(input, token);
                        if (input.Length != record.Backup.Length || !Convert.ToHexString(hash).Equals(record.Backup.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Архив цепочки изменён после регистрации.");
                        input.Position = 0;
                        using var archive = new ZipFile(input) { IsStreamOwner = false, Password = password };
                        manifest = await FileBackupZip.ReadManifestAsync(archive, password, token);
                    }
                    else
                    {
                        if (recoveryStorage is null) throw new IOException("Для восстановления индекса нужен доступ к удалённому хранилищу.");
                        var work = Path.Combine(Path.GetFullPath(recoveryWorkRoot ?? job.TemporaryDirectory ?? Path.GetTempPath()),
                            "index-recovery-" + Guid.NewGuid().ToString("N"));
                        PrivateBackupDirectory.Create(work);
                        try
                        {
                            var path = Path.Combine(work, "ancestor.zip");
                            await new BackupCopyExporter(recoveryStorage).ExportAsync(record, job, path, token);
                            manifest = await FileBackupZip.ReadManifestAsync(path, password, token);
                        }
                        finally { Directory.Delete(work, recursive: true); }
                    }
                    if (manifest.GetSnapshot() != record.Backup.FileSnapshot)
                        throw new InvalidDataException("Индекс архива не совпадает с зарегистрированной цепочкой.");
                    if (record.Id == tip.Record.Id) recovered = manifest;
                }
                if (recovered is not null) break;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException
                or CryptographicException or ZipException or InvalidOperationException)
            {
                DiagnosticLog.Current.Write(DiagnosticEvent.SnapshotRecoveryFailed, DiagnosticLevel.Warning, error);
                recovered = null;
            }
        }
        if (recovered is not null)
        {
            try { await snapshots.SaveAsync(job.Id, recovered, token); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { DiagnosticLog.Current.Write(DiagnosticEvent.SnapshotCacheFailed, DiagnosticLevel.Warning, error); }
            DiagnosticLog.Current.Write(DiagnosticEvent.SnapshotCacheRecovered, values: new(Count: tips.Sum(tip => tip.Chain.Count)));
        }
        return recovered;
    }
}
