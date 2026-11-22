using System.Text.Json;
using System.Text.Json.Serialization;

namespace SPBackup.Core;

public sealed record FileRecoveryKitSummary(Guid SnapshotId, DateTimeOffset CreatedUtc, int Archives, bool Encrypted, bool Incomplete);

/// <summary>A portable manifest and byte-identical archives. No profile settings or stored passwords travel with the kit.</summary>
public sealed class FileRecoveryKit(IBackupStorage storage)
{
    public const string ManifestName = "recovery.json";
    private const int MaxManifestBytes = 512 * 1024;
    private const string ManualPasswordKey = "portable-recovery";
    private sealed record Archive(FileBackupSnapshot Snapshot, DateTimeOffset CreatedUtc, long Length, string Sha256);
    private sealed record Manifest(int Format, Guid Selected, bool Encrypted, IReadOnlyList<Archive> Archives);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, MaxDepth = 12, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, AllowDuplicateProperties = false
    };

    public async Task<string> ExportAsync(BackupRecord selected, BackupJob job, BackupCatalog catalog, string parentDirectory,
        bool permitOtherDestinations = false, CancellationToken token = default, IProgress<BackupExportProgress>? progress = null,
        long? maximumBytes = null)
    {
        var parent = ExistingFolder(parentDirectory);
        if (job.Kind == DatabaseKind.FilesAndFolders && Directory.Exists(job.Source))
            FileBackupPaths.RequireOutside(job.Source, parent, "Для комплекта выберите папку вне исходных данных.");
        var records = await catalog.ReadAsync(token);
        var plan = new FileBackupReadPlan(selected, job, records, permitOtherDestinations);
        var chain = plan.Chain;
        if (maximumBytes is { } maximum && chain.Sum(record => record.Backup.Length) > maximum)
            throw new InvalidDataException("Комплект превышает допустимый размер.");
        var manifest = new Manifest(1, chain[^1].Backup.FileSnapshot!.SnapshotId, selected.Backup.ArchivePasswordKey is not null,
            chain.Select(record => new Archive(record.Backup.FileSnapshot!, record.CompletedUtc.ToUniversalTime(), record.Backup.Length, record.Backup.Sha256)).ToArray());
        Validate(manifest);
        var nonce = Guid.NewGuid().ToString("N");
        var stage = Path.Combine(parent, ".sp-recovery-" + nonce + ".partial");
        var result = Path.Combine(parent, "SP-Backup-recovery-" + nonce);
        PrivateBackupDirectory.Create(stage);
        var published = false;
        try
        {
            var exporter = new BackupCopyExporter(storage);
            foreach (var record in chain)
                await plan.ExportAsync(record, job, exporter, ArchivePath(stage, record.Backup.FileSnapshot!.SnapshotId), token, progress);
            await using (var stream = new FileStream(Path.Combine(stage, ManifestName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, Json, token);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            FileBackupRestorer.RejectAncestorReparse(parent);
            Directory.Move(stage, result);
            published = true;
            return result;
        }
        finally { if (!published) Directory.Delete(stage, recursive: true); }
    }

    public static async Task<FileRecoveryKitSummary> InspectAsync(string kitDirectory, CancellationToken token = default)
    {
        var manifest = await ReadAsync(ExistingFolder(kitDirectory), token);
        var point = manifest.Archives[^1];
        return new(manifest.Selected, point.CreatedUtc, manifest.Archives.Count, manifest.Encrypted, point.Snapshot.IsIncomplete);
    }

    public static async Task<RestoredFileBackup> RestoreAsync(string kitDirectory, string parentDirectory, string workRoot,
        string? password, CancellationToken token = default, IProgress<FileRestoreProgress>? progress = null)
    {
        var kit = ExistingFolder(kitDirectory);
        var parent = ExistingFolder(parentDirectory);
        var work = Path.GetFullPath(workRoot);
        FileBackupPaths.RequireOutside(kit, parent, "Выберите папку восстановления вне комплекта.");
        FileBackupPaths.RequireOutside(kit, work, "Укажите временную папку вне комплекта.");
        var manifest = await ReadAsync(kit, token);
        if (manifest.Encrypted && password is null) throw new InvalidDataException("Введите пароль зашифрованного комплекта.");
        if (manifest.Encrypted) ArchivePasswords.Validate(password!);
        var privateWork = Path.Combine(work, "portable-restore-" + Guid.NewGuid().ToString("N"));
        FileBackupRestorer.RejectAncestorReparse(privateWork);
        PrivateBackupDirectory.Create(privateWork);
        try
        {
            var job = new BackupJob { Name = "Восстановление из комплекта", Kind = DatabaseKind.FilesAndFolders, Source = kit,
                DestinationDirectory = kit, ScheduleKind = ScheduleKind.Manual };
            var records = manifest.Archives.Select(archive => BackupCatalog.CreateRecord(job,
                new(ArchivePath(kit, archive.Snapshot.SnapshotId), archive.Length, archive.Sha256)
                { FileSnapshot = archive.Snapshot, ArchivePasswordKey = manifest.Encrypted ? ManualPasswordKey : null }, archive.CreatedUtc)).ToArray();
            foreach (var record in records) FileBackupRestorer.RejectAncestorReparse(record.Backup.Location);
            var catalogPath = Path.Combine(privateWork, "copies.json");
            await using (var stream = new FileStream(catalogPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, records, cancellationToken: token);
            return await new FileBackupRestorer(new FileBackupStorage(), new BackupCatalog(catalogPath), privateWork)
                .RestoreAsync(records[^1], job, parent, token, manifest.Encrypted ? password : null, progress);
        }
        finally { Directory.Delete(privateWork, recursive: true); }
    }

    private static async Task<Manifest> ReadAsync(string kit, CancellationToken token)
    {
        var path = Path.Combine(kit, ManifestName);
        FileBackupRestorer.RejectAncestorReparse(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length is < 1 or > MaxManifestBytes) throw new InvalidDataException("Некорректный размер описания комплекта.");
        var manifest = await JsonSerializer.DeserializeAsync<Manifest>(stream, Json, token)
            ?? throw new InvalidDataException("Описание комплекта пустое.");
        Validate(manifest);
        foreach (var archive in manifest.Archives)
        {
            var archivePath = ArchivePath(kit, archive.Snapshot.SnapshotId);
            FileBackupRestorer.RejectAncestorReparse(archivePath);
            if (!File.Exists(archivePath)) throw new FileNotFoundException("В комплекте отсутствует архив " + Path.GetFileName(archivePath));
        }
        return manifest;
    }

    private static void Validate(Manifest manifest)
    {
        if (manifest.Format != 1 || manifest.Archives is not { Count: >= 1 and <= 365 }
            || manifest.Archives.Any(archive => archive?.Snapshot is null || archive.CreatedUtc == default
                || archive.CreatedUtc.Offset != TimeSpan.Zero || archive.Length <= 0
                || archive.Sha256 is not { Length: 64 } || archive.Sha256.Any(character => !Uri.IsHexDigit(character))))
            throw new InvalidDataException("Некорректное описание комплекта восстановления.");
        var root = manifest.Archives[0].Snapshot;
        for (var index = 0; index < manifest.Archives.Count; index++)
        {
            var snapshot = manifest.Archives[index].Snapshot;
            snapshot.Validate();
            if (snapshot.Depth != index || snapshot.FullSnapshotId != root.SnapshotId || snapshot.SourceIdentity != root.SourceIdentity
                || snapshot.SourceIsFile != root.SourceIsFile
                || snapshot.ParentSnapshotId != (index == 0 ? (Guid?)null : manifest.Archives[index - 1].Snapshot.SnapshotId))
                throw new InvalidDataException("Комплект содержит неполную или несовместимую последовательность архивов.");
        }
        if (manifest.Archives[^1].Snapshot.SnapshotId != manifest.Selected
            || manifest.Archives.Select(archive => archive.Snapshot.SnapshotId).Distinct().Count() != manifest.Archives.Count)
            throw new InvalidDataException("Выбранная точка комплекта не совпадает с цепочкой.");
    }

    private static string ArchivePath(string folder, Guid snapshot) => Path.Combine(folder, snapshot.ToString("N") + ".zip");

    private static string ExistingFolder(string folder)
    {
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder)) throw new DirectoryNotFoundException("Выберите существующую папку.");
        var path = Path.GetFullPath(folder);
        FileBackupRestorer.RejectAncestorReparse(path);
        return path;
    }
}
