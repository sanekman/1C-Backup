using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.Zip;

namespace SPBackup.Core;

public sealed record RestoredFileBackup(string Directory, int Files, int Archives, Guid SnapshotId)
{
    public IReadOnlyList<string>? SkippedPaths { get; init; }
}
public sealed record FileRestoreItem(string Path, bool IsDirectory, bool FromEarlierSnapshot);

/// <summary>Restores a verified chain into an owned new directory; existing files are never overwritten.</summary>
public sealed class FileBackupRestorer(IBackupStorage storage, BackupCatalog catalog, string defaultWorkRoot,
    IArchivePasswordStore? passwords = null)
{
    public async Task<IReadOnlyList<FileRestoreItem>> ReadContentsAsync(BackupRecord selected, BackupJob job,
        CancellationToken cancellationToken = default, string? passwordOverride = null, bool allowOtherDestinations = false)
    {
        await ReadChainAsync(selected, job, allowOtherDestinations, cancellationToken);
        var password = await GetPasswordAsync(selected, passwordOverride, cancellationToken);
        var work = WorkDirectory(job, Guid.NewGuid().ToString("N"));
        PrivateBackupDirectory.Create(work);
        try
        {
            var archivePath = Path.Combine(work, "point.zip");
            var manifest = await ReadArchiveAsync(selected, job, archivePath, password, cancellationToken);
            var skipped = (manifest.SkippedPaths ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return manifest.Directories.Select(path => new FileRestoreItem(path, true, false))
                .Concat(manifest.Files.Select(file => new FileRestoreItem(file.Path, false, skipped.Contains(file.Path))))
                .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    public async Task<RestoredFileBackup> RestoreAsync(BackupRecord selected, BackupJob job, string parentDirectory,
        CancellationToken cancellationToken = default, string? passwordOverride = null, IProgress<FileRestoreProgress>? progress = null,
        IReadOnlyCollection<string>? selectedPaths = null, bool allowOtherDestinations = false)
    {
        DiagnosticLog.Current.Write(DiagnosticEvent.FileRestoreStarted, jobId: job.Id, sourceKind: job.Kind);
        cancellationToken.ThrowIfCancellationRequested();
        var chain = await ReadChainAsync(selected, job, allowOtherDestinations, cancellationToken);
        var selection = selectedPaths?.ToHashSet(StringComparer.Ordinal);
        if (selection is { Count: 0 }) throw new InvalidDataException("Выберите файл или папку для восстановления.");
        var ancestors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in selection ?? [])
        {
            FileBackupPaths.Resolve(parentDirectory, path);
            for (var at = path.LastIndexOf('/'); at > 0; at = path.LastIndexOf('/', at - 1))
                ancestors.Add(path[..at]);
        }
        bool Included(string path)
        {
            if (selection is null || selection.Contains(path)) return true;
            for (var at = path.LastIndexOf('/'); at > 0; at = path.LastIndexOf('/', at - 1))
                if (selection.Contains(path[..at])) return true;
            return false;
        }
        var reporter = new FileRestoreReporter(progress);
        if (!Path.IsPathFullyQualified(parentDirectory) || !Directory.Exists(parentDirectory))
            throw new DirectoryNotFoundException("Выберите существующую папку для восстановления.");
        RejectAncestorReparse(parentDirectory);
        if (Directory.Exists(job.Source))
            FileBackupPaths.RequireOutside(job.Source, parentDirectory, "Для восстановления выберите папку вне исходных данных.");
        var password = await GetPasswordAsync(selected, passwordOverride, cancellationToken);
        var nonce = Guid.NewGuid().ToString("N");
        var stage = Path.Combine(parentDirectory, ".onec-restore-" + nonce + ".partial");
        var result = Path.Combine(parentDirectory, "Restored-files-" + nonce);
        var work = WorkDirectory(job, nonce);
        if (Directory.Exists(stage) || File.Exists(stage))
            throw new IOException("Временная папка восстановления уже существует.");
        var ownsStage = false; var ownsWork = false;
        FileBackupManifest? final = null;
        try
        {
            PrivateBackupDirectory.Create(work); ownsWork = true;
            PrivateBackupDirectory.Create(stage); ownsStage = true;
            var archiveNumber = 0;
            foreach (var record in chain)
            {
                cancellationToken.ThrowIfCancellationRequested();
                archiveNumber++;
                reporter.Report(FileRestorePhase.PreparingArchive, archiveNumber, chain.Count, 0, record.Backup.Length, true);
                var archivePath = Path.Combine(work, record.Id.ToString("N") + ".zip");
                var manifest = await ReadArchiveAsync(record, job, archivePath, password, cancellationToken, reporter.ForArchive(archiveNumber, chain.Count));
                var changed = manifest.ChangedFiles.Where(Included).ToArray();
                reporter.Report(FileRestorePhase.ApplyingSnapshot, archiveNumber, chain.Count, 0, changed.Length, true);
                var currentFiles = manifest.Files.Where(file => Included(file.Path)).Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
                // All paths inside stage belong to this restore, including files removed by an increment.
                foreach (var file in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RejectAncestorReparse(file);
                    var relative = Path.GetRelativePath(stage, file).Replace(Path.DirectorySeparatorChar, '/');
                    if (!currentFiles.Contains(relative)) File.Delete(file);
                }
                var currentDirectories = manifest.Directories.Where(path => ancestors.Contains(path) || Included(path)).ToHashSet(StringComparer.Ordinal);
                foreach (var directory in Directory.EnumerateDirectories(stage, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RejectAncestorReparse(directory);
                    var relative = Path.GetRelativePath(stage, directory).Replace(Path.DirectorySeparatorChar, '/');
                    if (!currentDirectories.Contains(relative)) Directory.Delete(directory, recursive: false);
                }
                foreach (var directory in currentDirectories.OrderBy(path => path.Length))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = FileBackupPaths.Resolve(stage, directory); RejectAncestorReparse(path); Directory.CreateDirectory(path);
                }
                using (var zip = new ZipFile(archivePath) { Password = password })
                {
                    var files = manifest.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
                    var applied = 0;
                    foreach (var name in changed)
                    {
                        var path = FileBackupPaths.Resolve(stage, name); RejectAncestorReparse(path);
                        await using var input = zip.GetInputStream(zip.GetEntry("data/" + name));
                        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                        await FileBackupZip.CopyVerifiedAsync(input, output, files[name], cancellationToken);
                        await output.FlushAsync(cancellationToken); output.Flush(true);
                        reporter.Report(FileRestorePhase.ApplyingSnapshot, archiveNumber, chain.Count, ++applied, changed.Length);
                    }
                }
                final = manifest; File.Delete(archivePath);
            }
            if (selection is not null)
            {
                var names = final!.Files.Select(file => file.Path).Concat(final.Directories).ToHashSet(StringComparer.Ordinal);
                if (!selection.IsSubsetOf(names))
                    throw new InvalidDataException("Один из выбранных путей отсутствует в этом снимке.");
            }
            var filesToVerify = final!.Files.Where(file => Included(file.Path)).ToArray();
            var verified = 0;
            reporter.Report(FileRestorePhase.VerifyingFiles, chain.Count, chain.Count, 0, filesToVerify.Length, true);
            foreach (var file in filesToVerify)
            {
                var path = FileBackupPaths.Resolve(stage, file.Path); RejectAncestorReparse(path);
                await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken));
                    if (input.Length != file.Length || !hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Восстановленная цепочка не совпадает с итоговым индексом: " + file.Path);
                }
                File.SetLastWriteTimeUtc(path, file.LastWriteUtc.UtcDateTime);
                reporter.Report(FileRestorePhase.VerifyingFiles, chain.Count, chain.Count, ++verified, filesToVerify.Length);
            }
            cancellationToken.ThrowIfCancellationRequested(); RejectAncestorReparse(parentDirectory);
            reporter.Report(FileRestorePhase.Publishing, chain.Count, chain.Count, 0, 1, true);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stage, result); ownsStage = false;
            reporter.Report(FileRestorePhase.Completed, chain.Count, chain.Count, 1, 1, true);
            DiagnosticLog.Current.Write(DiagnosticEvent.FileRestoreCompleted, jobId: job.Id, sourceKind: job.Kind, values: new(Count: filesToVerify.Length, Code: chain.Count));
            return new(result, filesToVerify.Length, chain.Count, final.SnapshotId)
                { SkippedPaths = selection is null ? final.SkippedPaths : final.SkippedPaths?.Where(Included).ToArray() };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { DiagnosticLog.Current.Write(DiagnosticEvent.FileRestoreCancelled, jobId: job.Id); throw; }
        catch (Exception ex)
        { DiagnosticLog.Current.Write(DiagnosticEvent.FileRestoreFailed, DiagnosticLevel.Error, ex, jobId: job.Id); throw; }
        finally
        {
            if (ownsStage) Directory.Delete(stage, recursive: true);
            if (ownsWork) Directory.Delete(work, recursive: true);
        }
    }

    private async Task<IReadOnlyList<BackupRecord>> ReadChainAsync(BackupRecord selected, BackupJob job,
        bool allowOtherDestinations, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (selected.Backup.FileSnapshot is null) throw new InvalidDataException("Выбранная копия не является файловым снимком.");
        BackupCopyVerifier.GetStorageJob(selected, job);
        var records = await catalog.ReadAsync(token);
        if (!records.Contains(selected)) throw new InvalidDataException("Выбранная копия больше не зарегистрирована.");
        return allowOtherDestinations ? FileBackupChains.ResolveAcrossStorages(selected, job, records, checkLocalAvailability: true)
            : FileBackupChains.Resolve(selected, records);
    }

    private async Task<string?> GetPasswordAsync(BackupRecord selected, string? passwordOverride, CancellationToken token)
    {
        if (passwordOverride is not null) ArchivePasswords.Validate(passwordOverride);
        return selected.Backup.ArchivePasswordKey is { } key
            ? passwordOverride ?? await ArchivePasswords.ResolveAsync(key, passwords, token)
            : null;
    }

    private string WorkDirectory(BackupJob job, string nonce)
    {
        var work = Path.Combine(Path.GetFullPath(job.TemporaryDirectory ?? defaultWorkRoot), "file-restore-" + nonce);
        if (Directory.Exists(job.Source))
            FileBackupPaths.RequireOutside(job.Source, work, "Временная папка восстановления должна находиться вне исходных данных.");
        if (Directory.Exists(work) || File.Exists(work)) throw new IOException("Временная папка восстановления уже существует.");
        RejectAncestorReparse(work);
        return work;
    }

    private async Task<FileBackupManifest> ReadArchiveAsync(BackupRecord record, BackupJob job, string path, string? password,
        CancellationToken token, IProgress<BackupExportProgress>? progress = null)
    {
        await new BackupCopyExporter(storage).ExportAsync(record, job, path, token, progress);
        var manifest = await FileBackupZip.ReadManifestAsync(path, password, token);
        if (manifest.GetSnapshot() != record.Backup.FileSnapshot)
            throw new InvalidDataException("Индекс архива не совпадает с зарегистрированной файловой цепочкой.");
        return manifest;
    }
    private static void RejectAncestorReparse(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(current) || Directory.Exists(current)) FileBackupPaths.RejectReparse(current);
            current = Path.GetDirectoryName(current);
        }
    }
}
