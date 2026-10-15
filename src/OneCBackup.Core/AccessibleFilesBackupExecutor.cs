using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.Zip;

namespace OneCBackup.Core;

/// <summary>On a source read failure, produces an independent full snapshot with explicit omissions.</summary>
public sealed class AccessibleFilesBackupExecutor(IBackupExecutor normal, IArchivePasswordStore? passwords = null,
    Func<string, IEnumerable<string>>? enumerateSource = null, Func<string, Stream>? openSource = null) : IBackupExecutor
{
    public async Task<string> CreateAsync(BackupJob job, string workDirectory, CancellationToken token)
    {
        if (job.Kind != DatabaseKind.FilesAndFolders) throw new ArgumentException("Выберите файлы и папки.");
        try { return await normal.CreateAsync(job, workDirectory, token); }
        catch (Exception ex) when (ex is FileBackupSourceException || IsAccessFailure(ex))
        { return await CreateAccessibleAsync(job, workDirectory, token); }
    }

    private static bool IsAccessFailure(Exception error) => error is UnauthorizedAccessException or System.Security.SecurityException
        || error is IOException && (error.HResult & 0xFFFF) is 5 or 32 or 33;

    private sealed record Scan(IReadOnlyList<FileBackupSourceFile> Files, IReadOnlyList<string> Directories, IReadOnlyList<string> Skipped);
    private Scan ReadSource(string source, bool isFile, CancellationToken token)
    {
        if (isFile)
        {
            FileBackupPaths.RejectReparse(source);
            var name = Path.GetFileName(source); FileBackupManifest.ValidateRelativePath(name);
            return new([new(source, name)], [], []);
        }
        var files = new List<FileBackupSourceFile>(); var directories = new List<string>(); var skipped = new List<string>();
        var pending = new Stack<string>(); pending.Push(source);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            string[] entries;
            try
            {
                FileBackupPaths.RejectReparse(directory);
                entries = (enumerateSource?.Invoke(directory) ?? Directory.EnumerateFileSystemEntries(directory)).ToArray();
            }
            catch (Exception ex) when (FileBackupSourceException.IsReadFailure(ex))
            { skipped.Add(directory == source ? "." : Relative(source, directory)); continue; }
            foreach (var child in entries)
            {
                token.ThrowIfCancellationRequested();
                var relative = Relative(source, child); FileBackupManifest.ValidateRelativePath(relative);
                FileAttributes attributes;
                try { FileBackupPaths.RejectReparse(child); attributes = File.GetAttributes(child); }
                catch (Exception ex) when (FileBackupSourceException.IsReadFailure(ex)) { skipped.Add(relative); continue; }
                if (attributes.HasFlag(FileAttributes.Directory)) { directories.Add(relative); pending.Push(child); }
                else files.Add(new(child, relative));
                if (files.Count > FileBackupManifest.MaximumItems || directories.Count > FileBackupManifest.MaximumItems || skipped.Count > FileBackupManifest.MaximumItems)
                    throw new InvalidDataException("Одна файловая копия поддерживает до 1000000 файлов, папок и пропусков каждого вида.");
            }
        }
        if (skipped.Count > FileBackupManifest.MaximumItems)
            throw new InvalidDataException("Одна файловая копия поддерживает до 1000000 пропусков.");
        return new(files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray(),
            directories.Order(StringComparer.Ordinal).ToArray(), skipped.Order(StringComparer.Ordinal).ToArray());
    }
    private static string Relative(string source, string path) => Path.GetRelativePath(source, path).Replace(Path.DirectorySeparatorChar, '/');

    private async Task<string> CreateAccessibleAsync(BackupJob job, string workDirectory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (job.Validate().Count > 0) throw new InvalidDataException("Некорректное задание.");
        var source = Path.GetFullPath(job.Source); var isFile = File.Exists(source);
        if (!isFile && !Directory.Exists(source)) throw new FileNotFoundException("Источник недоступен или не существует.");
        if (!isFile)
        {
            FileBackupPaths.RequireOutside(source, workDirectory, "Рабочая папка должна находиться вне источника.");
            foreach (var target in job.StorageJobs.Where(target => target.StorageKind is StorageKind.LocalDirectory or StorageKind.NetworkShare))
                FileBackupPaths.RequireOutside(source, target.DestinationDirectory, "Хранилище должно находиться вне источника.");
        }
        var password = await ArchivePasswords.ResolveAsync(job.ArchivePasswordKey, passwords, token);
        BackupProgressContext.Report(BackupStage.ScanningSource);
        var scan = ReadSource(source, isFile, token); var skipped = new HashSet<string>(scan.Skipped, StringComparer.OrdinalIgnoreCase);
        var lengths = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var item in scan.Files)
        {
            token.ThrowIfCancellationRequested();
            try { lengths.Add(item.RelativePath, new FileInfo(item.FullPath).Length); }
            catch (Exception ex) when (FileBackupSourceException.IsReadFailure(ex)) { skipped.Add(item.RelativePath); }
        }
        var archiveProgress = new FileArchiveProgress(lengths.Values.Sum());
        archiveProgress.Report();
        var files = new List<FileBackupEntry>(); var id = Guid.NewGuid();
        Directory.CreateDirectory(workDirectory);
        var destination = Path.Combine(Path.GetFullPath(workDirectory), BackupFileName.Create(job, ".files-full.zip", uniqueId: id));
        var temporary = destination + ".partial";
        var staging = Path.Combine(Path.GetFullPath(workDirectory), "source-stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            PrivateBackupDirectory.Create(staging);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                using (var zip = new ZipOutputStream(output) { IsStreamOwner = false, Password = password, UseZip64 = UseZip64.Dynamic })
                {
                    zip.SetLevel(6);
                    foreach (var item in scan.Files)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!lengths.TryGetValue(item.RelativePath, out var plannedLength)) continue;
                        var staged = Path.Combine(staging, "file.tmp");
                        try
                        {
                            FileBackupEntry descriptor;
                            try
                            {
                                descriptor = await StageAsync(item, plannedLength, staged, token);
                            }
                            catch (FileBackupSourceException)
                            { skipped.Add(item.RelativePath); archiveProgress.Advance(plannedLength); continue; }
                            // The ZIP only receives a complete, validated local file. Source
                            // errors cannot leave a half-written entry in the final archive.
                            await using var input = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                                FileOptions.Asynchronous | FileOptions.SequentialScan);
                            zip.PutNextEntry(Entry("data/" + item.RelativePath, descriptor.Length, password));
                            await archiveProgress.CopyAsync(input, zip, token); zip.CloseEntry();
                            files.Add(descriptor);
                            archiveProgress.Advance(plannedLength);
                        }
                        finally { File.Delete(staged); }
                    }
                    var manifest = new FileBackupManifest
                    {
                        SnapshotId = id, FullSnapshotId = id, SourceIdentity = FileBackupManifest.IdentifySource(source, isFile), SourceIsFile = isFile,
                        Files = files, Directories = scan.Directories, ChangedFiles = files.Select(file => file.Path).ToArray(),
                        FormatVersion = skipped.Count > 0 ? 2 : 1, SkippedPaths = skipped.Count > 0 ? skipped.Order(StringComparer.Ordinal).ToArray() : null
                    };
                    manifest.Validate();
                    await FileBackupZip.WriteManifestAsync(zip, manifest, password, token); zip.Finish();
                }
                await output.FlushAsync(token); output.Flush(true);
            }
            // Each entry describes its validated staged version. Later edits or
            // deletions in the source do not invalidate data already captured.
            BackupProgressContext.Report(BackupStage.Verifying);
            await FileBackupZip.VerifyAsync(temporary, password, token);
            token.ThrowIfCancellationRequested(); File.Move(temporary, destination); return destination;
        }
        finally { File.Delete(temporary); if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    private async Task<FileBackupEntry> StageAsync(FileBackupSourceFile item, long plannedLength, string staged, CancellationToken token)
    {
        FileBackupSourceException.Read(() => { FileBackupPaths.RejectReparse(item.FullPath); return true; });
        await using var input = new FileBackupSourceStream(FileBackupSourceException.Read(() => openSource?.Invoke(item.FullPath)
            ?? new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan)), item.RelativePath, plannedLength);
        var modified = FileBackupSourceException.Read(() => File.GetLastWriteTimeUtc(item.FullPath));
        if (input.Length != plannedLength)
            throw new FileBackupSourceException("Размер файла изменился после сканирования: " + item.RelativePath);
        await using var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920]; int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        { hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), token); }
        var info = new FileInfo(item.FullPath);
        if (input.Length != plannedLength || FileBackupSourceException.Read(() => info.Length) != plannedLength
            || FileBackupSourceException.Read(() => info.LastWriteTimeUtc) != modified)
            throw new FileBackupSourceException("Файл изменился во время копирования: " + item.RelativePath);
        await output.FlushAsync(token);
        return new(item.RelativePath, plannedLength, Convert.ToHexStringLower(hash.GetHashAndReset()), new(modified));
    }
    private static ZipEntry Entry(string name, long length, string? password) => new(name)
    { Size = length, AESKeySize = password is null ? 0 : 256, DateTime = DateTime.UtcNow };
}
