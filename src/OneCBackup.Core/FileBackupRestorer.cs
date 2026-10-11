using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.Zip;

namespace OneCBackup.Core;

public sealed record RestoredFileBackup(string Directory, int Files, int Archives, Guid SnapshotId);

/// <summary>Restores a verified chain into an owned new directory; existing files are never overwritten.</summary>
public sealed class FileBackupRestorer(IBackupStorage storage, BackupCatalog catalog, string defaultWorkRoot,
    IArchivePasswordStore? passwords = null)
{
    public async Task<RestoredFileBackup> RestoreAsync(BackupRecord selected, BackupJob job, string parentDirectory,
        CancellationToken cancellationToken = default, string? passwordOverride = null, IProgress<FileRestoreProgress>? progress = null)
    {
        DiagnosticLog.Current.Write(DiagnosticEvent.FileRestoreStarted, jobId: job.Id, sourceKind: job.Kind);
        cancellationToken.ThrowIfCancellationRequested();
        if (selected.Backup.FileSnapshot is null) throw new InvalidDataException("Выбранная копия не является файловым снимком.");
        BackupCopyVerifier.GetStorageJob(selected, job);
        var records = await catalog.ReadAsync(cancellationToken);
        if (!records.Contains(selected)) throw new InvalidDataException("Выбранная копия больше не зарегистрирована.");
        var chain = FileBackupChains.Resolve(selected, records);
        var reporter = new FileRestoreReporter(progress);
        if (!Path.IsPathFullyQualified(parentDirectory) || !Directory.Exists(parentDirectory))
            throw new DirectoryNotFoundException("Выберите существующую папку для восстановления.");
        RejectAncestorReparse(parentDirectory);
        if (Directory.Exists(job.Source))
            FileBackupPaths.RequireOutside(job.Source, parentDirectory, "Для восстановления выберите папку вне исходных данных.");
        if (passwordOverride is not null) ArchivePasswords.Validate(passwordOverride);
        var password = selected.Backup.ArchivePasswordKey is null ? null
            : passwordOverride ?? await ArchivePasswords.ResolveAsync(selected.Backup.ArchivePasswordKey, passwords, cancellationToken);
        var nonce = Guid.NewGuid().ToString("N");
        var stage = Path.Combine(parentDirectory, ".onec-restore-" + nonce + ".partial");
        var result = Path.Combine(parentDirectory, "Restored-files-" + nonce);
        var work = Path.Combine(Path.GetFullPath(job.TemporaryDirectory ?? defaultWorkRoot), "file-restore-" + nonce);
        if (Directory.Exists(job.Source))
            FileBackupPaths.RequireOutside(job.Source, work, "Временная папка восстановления должна находиться вне исходных данных.");
        if (Directory.Exists(stage) || File.Exists(stage) || Directory.Exists(work) || File.Exists(work))
            throw new IOException("Временная папка восстановления уже существует.");
        var ownsStage = false; var ownsWork = false;
        FileBackupManifest? final = null;
        try
        {
            Directory.CreateDirectory(work); ownsWork = true;
            Directory.CreateDirectory(stage); ownsStage = true;
            var archiveNumber = 0;
            foreach (var record in chain)
            {
                cancellationToken.ThrowIfCancellationRequested();
                archiveNumber++;
                reporter.Report(FileRestorePhase.PreparingArchive, archiveNumber, chain.Count, 0, record.Backup.Length, true);
                var archivePath = Path.Combine(work, record.Id.ToString("N") + ".zip");
                await new BackupCopyExporter(storage).ExportAsync(record, job, archivePath, cancellationToken, reporter.ForArchive(archiveNumber, chain.Count));
                var manifest = await FileBackupZip.ReadManifestAsync(archivePath, password, cancellationToken);
                if (manifest.GetSnapshot() != record.Backup.FileSnapshot)
                    throw new InvalidDataException("Индекс архива не совпадает с зарегистрированной файловой цепочкой.");
                reporter.Report(FileRestorePhase.ApplyingSnapshot, archiveNumber, chain.Count, 0, manifest.ChangedFiles.Count, true);
                var currentFiles = manifest.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
                // All paths inside stage belong to this restore, including files removed by an increment.
                foreach (var file in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RejectAncestorReparse(file);
                    var relative = Path.GetRelativePath(stage, file).Replace(Path.DirectorySeparatorChar, '/');
                    if (!currentFiles.Contains(relative)) File.Delete(file);
                }
                var currentDirectories = manifest.Directories.ToHashSet(StringComparer.Ordinal);
                foreach (var directory in Directory.EnumerateDirectories(stage, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RejectAncestorReparse(directory);
                    var relative = Path.GetRelativePath(stage, directory).Replace(Path.DirectorySeparatorChar, '/');
                    if (!currentDirectories.Contains(relative)) Directory.Delete(directory, recursive: false);
                }
                foreach (var directory in manifest.Directories.OrderBy(path => path.Length))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = FileBackupPaths.Resolve(stage, directory); RejectAncestorReparse(path); Directory.CreateDirectory(path);
                }
                using (var zip = new ZipFile(archivePath) { Password = password })
                {
                    var files = manifest.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
                    var applied = 0;
                    foreach (var name in manifest.ChangedFiles)
                    {
                        var path = FileBackupPaths.Resolve(stage, name); RejectAncestorReparse(path);
                        await using var input = zip.GetInputStream(zip.GetEntry("data/" + name));
                        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                        await FileBackupZip.CopyVerifiedAsync(input, output, files[name], cancellationToken);
                        await output.FlushAsync(cancellationToken); output.Flush(true);
                        reporter.Report(FileRestorePhase.ApplyingSnapshot, archiveNumber, chain.Count, ++applied, manifest.ChangedFiles.Count);
                    }
                }
                final = manifest; File.Delete(archivePath);
            }
            var verified = 0;
            reporter.Report(FileRestorePhase.VerifyingFiles, chain.Count, chain.Count, 0, final!.Files.Count, true);
            foreach (var file in final.Files)
            {
                var path = FileBackupPaths.Resolve(stage, file.Path); RejectAncestorReparse(path);
                await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken));
                    if (input.Length != file.Length || !hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Восстановленная цепочка не совпадает с итоговым индексом: " + file.Path);
                }
                File.SetLastWriteTimeUtc(path, file.LastWriteUtc.UtcDateTime);
                reporter.Report(FileRestorePhase.VerifyingFiles, chain.Count, chain.Count, ++verified, final.Files.Count);
            }
            cancellationToken.ThrowIfCancellationRequested(); RejectAncestorReparse(parentDirectory);
            reporter.Report(FileRestorePhase.Publishing, chain.Count, chain.Count, 0, 1, true);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stage, result); ownsStage = false;
            reporter.Report(FileRestorePhase.Completed, chain.Count, chain.Count, 1, 1, true);
            DiagnosticLog.Current.Write(DiagnosticEvent.FileRestoreCompleted, jobId: job.Id, sourceKind: job.Kind, values: new(Count: final.Files.Count, Code: chain.Count));
            return new(result, final.Files.Count, chain.Count, final.SnapshotId);
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
