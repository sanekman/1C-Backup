namespace SPBackup.Core;

/// <summary>Backfills verified ancestors into a lagging destination without changing the full-copy plan.</summary>
internal sealed class FileBackupChainRepair(IBackupStorage storage, BackupCatalog catalog, IArchivePasswordStore? passwords)
{
    internal async Task<int> EnsureAsync(BackupJob job, BackupJob target, FileBackupSnapshot? snapshot,
        string workRoot, CancellationToken token)
    {
        if (snapshot?.ParentSnapshotId is not { } parent) return 0;
        var records = await catalog.ReadAsync(token);
        var allowed = job.StorageJobs.Select(StorageAddress.Identity).ToHashSet(StringComparer.Ordinal);
        var parents = records.Where(record => record.JobId == job.Id && allowed.Contains(record.StorageIdentity)
            && record.Backup.FileSnapshot?.SnapshotId == parent && record.Backup.ArchivePasswordKey == job.ArchivePasswordKey).ToArray();
        IReadOnlyList<BackupRecord>? chain = null;
        foreach (var candidate in parents)
        {
            try { chain = FileBackupChains.Resolve(candidate, records); break; }
            catch (InvalidDataException) { }
        }
        if (chain is null) throw new InvalidDataException("Не найдены предшествующие архивы инкремента. Полная копия вне плана не создаётся.");
        var targetIdentity = StorageAddress.Identity(target);
        if (chain.All(ancestor => records.Any(record => record.JobId == job.Id && record.StorageIdentity == targetIdentity
            && record.Backup.FileSnapshot?.SnapshotId == ancestor.Backup.FileSnapshot!.SnapshotId))) return 0;
        var copied = 0;
        var work = Path.Combine(Path.GetFullPath(workRoot), "chain-backfill-" + Guid.NewGuid().ToString("N"));
        var ownsWork = false;
        try
        {
            foreach (var ancestor in chain)
            {
                token.ThrowIfCancellationRequested();
                records = await catalog.ReadAsync(token);
                var id = ancestor.Backup.FileSnapshot!.SnapshotId;
                var existing = records.SingleOrDefault(record => record.JobId == job.Id && record.StorageIdentity == targetIdentity
                    && record.Backup.FileSnapshot?.SnapshotId == id);
                if (existing is not null)
                {
                    if (existing.Backup.FileSnapshot != ancestor.Backup.FileSnapshot || existing.Backup.ArchivePasswordKey != job.ArchivePasswordKey)
                        throw new InvalidDataException("Индекс предшествующего архива в назначении не совпадает с цепочкой.");
                    await storage.VerifyAsync(existing.Backup, target, token);
                    continue;
                }
                if (!ownsWork) { PrivateBackupDirectory.Create(work); ownsWork = true; }
                // A new name also preserves an unregistered file left by an interrupted delivery.
                var path = Path.Combine(work, id.ToString("N") + "-" + Guid.NewGuid().ToString("N") + ".files.chain.zip");
                Exception? last = null;
                BackupRecord? source = null;
                foreach (var candidate in records.Where(record => record.JobId == job.Id && allowed.Contains(record.StorageIdentity)
                    && record.Backup.FileSnapshot == ancestor.Backup.FileSnapshot && record.Backup.ArchivePasswordKey == job.ArchivePasswordKey))
                {
                    try
                    {
                        await new BackupCopyExporter(storage).ExportAsync(candidate, job, path, token);
                        var password = await ArchivePasswords.ResolveAsync(candidate.Backup.ArchivePasswordKey, passwords, token);
                        var manifest = await FileBackupZip.ReadManifestAsync(path, password, token);
                        if (manifest.GetSnapshot() != candidate.Backup.FileSnapshot)
                            throw new InvalidDataException("Индекс предшествующего архива не совпадает с каталогом.");
                        source = candidate; break;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                    { last = error; if (File.Exists(path)) File.Delete(path); }
                }
                if (source is null) throw new IOException("Не удалось получить проверенный предшествующий архив для назначения.", last);
                var stored = await storage.StoreAsync(path, target, token);
                if (stored.Length != source.Backup.Length || !stored.Sha256.Equals(source.Backup.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Доставленный предшествующий архив не совпадает с проверенным источником.");
                stored = stored with { FileSnapshot = source.Backup.FileSnapshot, ArchivePasswordKey = source.Backup.ArchivePasswordKey };
                // Preserve original full-copy time: catch-up must not shift the weekly full-copy date.
                await catalog.RegisterAsync(target, stored, source.CompletedUtc, CancellationToken.None);
                copied++; File.Delete(path);
            }
            return copied;
        }
        finally { if (ownsWork) Directory.Delete(work, recursive: true); }
    }
}
