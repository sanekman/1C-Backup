using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.Zip;

namespace OneCBackup.Core;

/// <summary>Captures each readable file once; retries source failures after the other entries.</summary>
public sealed class AccessibleFilesBackupExecutor(IBackupExecutor normal, IArchivePasswordStore? passwords = null,
    Func<string, IEnumerable<string>>? enumerateSource = null, Func<string, Stream>? openSource = null) : IBackupExecutor
{
    public async Task<string> CreateAsync(BackupJob job, string workDirectory, CancellationToken token)
    {
        if (job.Kind != DatabaseKind.FilesAndFolders) throw new ArgumentException("Выберите файлы и папки.");
        // The native executor already handles source errors without rebuilding the archive.
        if (normal is FileFolderBackupExecutor) return await normal.CreateAsync(job, workDirectory, token);
        try { return await normal.CreateAsync(job, workDirectory, token); }
        catch (Exception ex) when (ex is FileBackupSourceException || IsAccessFailure(ex))
        { return await CreateSnapshotAsync(job, workDirectory, token); }
    }

    private static bool IsAccessFailure(Exception error) => error is UnauthorizedAccessException or System.Security.SecurityException
        || error is IOException && (error.HResult & 0xFFFF) is 5 or 32 or 33;

    private sealed record Scan(IReadOnlyList<FileBackupSourceFile> Files, IReadOnlyList<string> Directories, IReadOnlyList<string> Skipped);
    private Scan ReadSource(string source, bool isFile, CancellationToken token, IReadOnlyList<string>? retryPaths = null)
    {
        var files = new Dictionary<string, FileBackupSourceFile>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(retryPaths ?? [source]);
        while (pending.TryPop(out var path))
        {
            token.ThrowIfCancellationRequested();
            var relative = isFile ? Path.GetFileName(source) : Relative(source, path);
            if (relative != ".") FileBackupManifest.ValidateRelativePath(relative);
            if (visited.TryGetValue(relative, out var existing))
            {
                // The portable manifest cannot represent both case variants.
                // Preserve the available entry, but mark the parent as incomplete.
                if (existing != relative)
                {
                    var slash = relative.LastIndexOf('/');
                    skipped.Add(slash < 0 ? "." : relative[..slash]);
                }
                continue;
            }
            visited.Add(relative, relative);
            try
            {
                FileBackupPaths.RejectReparse(path);
                if (File.GetAttributes(path).HasFlag(FileAttributes.Directory))
                {
                    if (relative != ".") directories.Add(relative);
                    // Keep entries already enumerated if a later MoveNext fails.
                    foreach (var child in enumerateSource?.Invoke(path) ?? Directory.EnumerateFileSystemEntries(path))
                    {
                        token.ThrowIfCancellationRequested();
                        pending.Push(child);
                        if (pending.Count > 2 * FileBackupManifest.MaximumItems)
                            throw new InvalidDataException("Слишком много путей в исходной папке.");
                    }
                }
                else files.TryAdd(relative, new(path, relative));
            }
            catch (Exception ex) when (FileBackupSourceException.IsReadFailure(ex))
            { skipped.Add(relative); }
            if (files.Count > FileBackupManifest.MaximumItems || directories.Count > FileBackupManifest.MaximumItems || skipped.Count > FileBackupManifest.MaximumItems)
                throw new InvalidDataException("Одна файловая копия поддерживает до 1000000 файлов, папок и пропусков каждого вида.");
        }
        return new(files.Values.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray(),
            directories.Order(StringComparer.Ordinal).ToArray(), skipped.Order(StringComparer.Ordinal).ToArray());
    }
    private static string Relative(string source, string path) => Path.GetRelativePath(source, path).Replace(Path.DirectorySeparatorChar, '/');

    internal async Task<string> CreateSnapshotAsync(BackupJob job, string workDirectory, CancellationToken token,
        FileBackupManifest? previous = null)
    {
        token.ThrowIfCancellationRequested();
        if (job.Validate().Count > 0) throw new InvalidDataException("Некорректное задание.");
        var source = Path.GetFullPath(job.Source); var isFile = File.Exists(source);
        if (!isFile && !Directory.Exists(source)) throw new FileNotFoundException("Источник недоступен или не существует.");
        if (!isFile)
        {
            FileBackupPaths.RequireOutside(source, workDirectory, "Временная папка должна находиться вне исходной папки.");
            foreach (var target in job.StorageJobs.Where(target => target.StorageKind is StorageKind.LocalDirectory or StorageKind.NetworkShare))
                FileBackupPaths.RequireOutside(source, target.DestinationDirectory, "Хранилища должны находиться вне исходной папки.");
        }
        var password = await ArchivePasswords.ResolveAsync(job.ArchivePasswordKey, passwords, token);
        BackupProgressContext.Report(BackupStage.ScanningSource);
        // Keep a single directory spelling throughout an incremental chain.
        // Otherwise an unreadable child can retain "Folder/file" while newly
        // captured siblings use "folder/other", which is unsafe to restore on Linux.
        var canonicalDirectories = previous?.Directories.ToDictionary(path => path, path => path, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string ArchiveDirectory(string path)
        {
            if (path == ".") return path;
            if (canonicalDirectories.TryGetValue(path, out var canonical)) return canonical;
            var slash = path.LastIndexOf('/');
            canonical = slash < 0 ? path : ArchiveDirectory(path[..slash]) + path[slash..];
            canonicalDirectories.Add(path, canonical);
            return canonical;
        }
        string ArchivePath(string path)
        {
            var slash = path.LastIndexOf('/');
            return slash < 0 ? path : ArchiveDirectory(path[..slash]) + path[slash..];
        }
        Scan ForArchive(Scan value) => new(value.Files.Select(item => item with { RelativePath = ArchivePath(item.RelativePath) }).ToArray(),
            value.Directories.Select(ArchiveDirectory).ToArray(),
            value.Skipped.Select(path => canonicalDirectories.TryGetValue(path, out var canonical) ? canonical : ArchivePath(path)).ToArray());
        var sourceScan = ReadSource(source, isFile, token);
        var scan = ForArchive(sourceScan);
        var oldFiles = previous?.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, FileBackupEntry>(StringComparer.OrdinalIgnoreCase);
        var lengths = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in scan.Files)
        {
            token.ThrowIfCancellationRequested();
            try { lengths.Add(item.RelativePath, new FileInfo(item.FullPath).Length); }
            catch (Exception ex) when (FileBackupSourceException.IsReadFailure(ex)) { lengths.Add(item.RelativePath, 0); }
        }
        var archiveProgress = new FileArchiveProgress(lengths.Values.Sum());
        archiveProgress.Report();
        var files = new Dictionary<string, FileBackupEntry>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(scan.Directories, StringComparer.OrdinalIgnoreCase);
        var changed = new List<string>();
        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var retry = new Dictionary<string, FileBackupSourceFile>(StringComparer.OrdinalIgnoreCase);
        var id = Guid.NewGuid();
        Directory.CreateDirectory(workDirectory);
        var destination = Path.Combine(Path.GetFullPath(workDirectory), BackupFileName.Create(job,
            previous is null ? ".files-full.zip" : ".files-incremental.zip", uniqueId: id));
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
                        if (!await CaptureAsync(item, lengths[item.RelativePath])) retry.TryAdd(item.RelativePath, item);
                        else archiveProgress.Advance(lengths[item.RelativePath]);
                    }
                    // Only failed enumeration paths are revisited; successful entries are never read again.
                    if (scan.Skipped.Count > 0)
                    {
                        var retriedScan = ForArchive(ReadSource(source, isFile, token, sourceScan.Skipped.Select(path =>
                            isFile || path == "." ? source : FileBackupPaths.Resolve(source, path)).ToArray()));
                        directories.UnionWith(retriedScan.Directories);
                        skipped.UnionWith(retriedScan.Skipped);
                        foreach (var item in retriedScan.Files)
                            if (!files.ContainsKey(item.RelativePath)) retry.TryAdd(item.RelativePath, item);
                    }
                    // One bounded retry, after every initially accessible file is in the ZIP.
                    foreach (var item in retry.Values.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
                    {
                        token.ThrowIfCancellationRequested();
                        long currentLength;
                        try { currentLength = FileBackupSourceException.Read(() => new FileInfo(item.FullPath).Length); }
                        catch (FileBackupSourceException) { skipped.Add(item.RelativePath); AdvanceRetry(item); continue; }
                        if (!await CaptureAsync(item, currentLength)) skipped.Add(item.RelativePath);
                        AdvanceRetry(item);
                    }
                    // A failed read is not a deletion. An increment retains the last captured version.
                    foreach (var old in oldFiles.Values)
                        if (FileBackupManifest.IsSkipped(old.Path, skipped) && !directories.Contains(old.Path) && !HasFileAncestor(old.Path))
                            files.TryAdd(old.Path, old);
                    if (previous is not null)
                        foreach (var directory in previous.Directories)
                            if (FileBackupManifest.IsSkipped(directory, skipped) && !HasFileAncestor(directory, includeSelf: true)) directories.Add(directory);
                    foreach (var path in files.Keys.ToArray())
                    {
                        var slash = path.LastIndexOf('/');
                        while (slash >= 0)
                        { directories.Add(path[..slash]); slash = path.LastIndexOf('/', slash - 1); }
                    }
                    var entries = files.Values.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
                    var manifest = new FileBackupManifest
                    {
                        SnapshotId = id, FullSnapshotId = previous?.FullSnapshotId ?? id, ParentSnapshotId = previous?.SnapshotId,
                        Depth = previous is null ? 0 : previous.Depth + 1,
                        SourceIdentity = FileBackupManifest.IdentifySource(source, isFile), SourceIsFile = isFile,
                        Files = entries, Directories = directories.Order(StringComparer.Ordinal).ToArray(),
                        ChangedFiles = changed.Order(StringComparer.Ordinal).ToArray(),
                        DeletedFiles = oldFiles.Keys.Where(path => !files.ContainsKey(path) && !FileBackupManifest.IsSkipped(path, skipped)).Order(StringComparer.Ordinal).ToArray(),
                        FormatVersion = skipped.Count == 0 ? 1
                            : previous is not null || files.Keys.Any(path => FileBackupManifest.IsSkipped(path, skipped)) ? 3 : 2,
                        SkippedPaths = skipped.Count > 0 ? skipped.Order(StringComparer.Ordinal).ToArray() : null
                    };
                    manifest.Validate();
                    await FileBackupZip.WriteManifestAsync(zip, manifest, password, token); zip.Finish();

                    void AdvanceRetry(FileBackupSourceFile item)
                    { if (lengths.TryGetValue(item.RelativePath, out var planned)) archiveProgress.Advance(planned); }
                    bool HasFileAncestor(string path, bool includeSelf = false)
                    {
                        if (includeSelf && files.ContainsKey(path)) return true;
                        var slash = path.LastIndexOf('/');
                        while (slash >= 0)
                        {
                            path = path[..slash];
                            if (files.ContainsKey(path)) return true;
                            slash = path.LastIndexOf('/');
                        }
                        return false;
                    }
                    async Task<bool> CaptureAsync(FileBackupSourceFile item, long plannedLength)
                    {
                        token.ThrowIfCancellationRequested();
                        var staged = Path.Combine(staging, "file.tmp");
                        var progressLength = lengths.GetValueOrDefault(item.RelativePath);
                        try
                        {
                            FileBackupEntry descriptor;
                            try { descriptor = await StageAsync(item, plannedLength, staged, archiveProgress, progressLength, token); }
                            catch (FileBackupSourceException) { return false; }
                            if (!oldFiles.TryGetValue(item.RelativePath, out var old) || old.Sha256 != descriptor.Sha256
                                || old.Length != descriptor.Length || old.Path != descriptor.Path)
                            {
                                // Source failures occur before opening a ZIP entry.
                                await using var input = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                                zip.PutNextEntry(Entry("data/" + item.RelativePath, descriptor.Length, password));
                                await archiveProgress.CopyStagedAsync(input, zip, progressLength, token); zip.CloseEntry();
                                changed.Add(item.RelativePath);
                            }
                            files[item.RelativePath] = descriptor;
                            return true;
                        }
                        finally { File.Delete(staged); }
                    }
                }
                await output.FlushAsync(token); output.Flush(true);
            }
            BackupProgressContext.Report(BackupStage.Verifying);
            await FileBackupZip.VerifyAsync(temporary, password, token);
            token.ThrowIfCancellationRequested(); File.Move(temporary, destination); return destination;
        }
        finally { File.Delete(temporary); if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    private async Task<FileBackupEntry> StageAsync(FileBackupSourceFile item, long plannedLength, string staged,
        FileArchiveProgress progress, long progressLength, CancellationToken token)
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
        var buffer = new byte[81920]; long read = 0; int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), token);
            read += count; progress.ReportWithinFile(Math.Min(read, progressLength) / 2);
        }
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
