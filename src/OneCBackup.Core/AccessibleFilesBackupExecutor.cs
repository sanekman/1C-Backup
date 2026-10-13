using System.Security.Cryptography;
using System.Text.Json;
using ICSharpCode.SharpZipLib.Zip;

namespace OneCBackup.Core;

/// <summary>On source access refusal, produces an independent full snapshot with explicit omissions.</summary>
public sealed class AccessibleFilesBackupExecutor(IBackupExecutor normal, IArchivePasswordStore? passwords = null,
    Func<string, IEnumerable<string>>? enumerateSource = null, Func<string, Stream>? openSource = null) : IBackupExecutor
{
    public async Task<string> CreateAsync(BackupJob job, string workDirectory, CancellationToken token)
    {
        if (job.Kind != DatabaseKind.FilesAndFolders) throw new ArgumentException("Выберите файлы и папки.");
        try { return await normal.CreateAsync(job, workDirectory, token); }
        catch (Exception ex) when (IsAccessFailure(ex)) { return await CreateAccessibleAsync(job, workDirectory, token); }
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
            catch (Exception ex) when (IsAccessFailure(ex))
            { skipped.Add(directory == source ? "." : Relative(source, directory)); continue; }
            foreach (var child in entries)
            {
                token.ThrowIfCancellationRequested();
                var relative = Relative(source, child); FileBackupManifest.ValidateRelativePath(relative);
                FileAttributes attributes;
                try { FileBackupPaths.RejectReparse(child); attributes = File.GetAttributes(child); }
                catch (Exception ex) when (IsAccessFailure(ex)) { skipped.Add(relative); continue; }
                if (attributes.HasFlag(FileAttributes.Directory)) { directories.Add(relative); pending.Push(child); }
                else files.Add(new(child, relative));
                if (files.Count > 100000 || directories.Count > 100000 || skipped.Count > 100000)
                    throw new InvalidDataException("Слишком много файлов, папок или пропусков в одном задании.");
            }
        }
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
        var files = new List<FileBackupEntry>(); var id = Guid.NewGuid();
        Directory.CreateDirectory(workDirectory);
        var destination = Path.Combine(Path.GetFullPath(workDirectory), BackupFileName.Create(job, ".files-full.zip", uniqueId: id));
        var temporary = destination + ".partial";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                using (var zip = new ZipOutputStream(output) { IsStreamOwner = false, Password = password, UseZip64 = UseZip64.Dynamic })
                {
                    zip.SetLevel(6); long processed = 0;
                    foreach (var item in scan.Files)
                    {
                        token.ThrowIfCancellationRequested();
                        Stream input; long length; DateTimeOffset modified; string sha;
                        try
                        {
                            FileBackupPaths.RejectReparse(item.FullPath);
                            input = openSource?.Invoke(item.FullPath) ?? new FileStream(item.FullPath, FileMode.Open, FileAccess.Read,
                                FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                            try
                            {
                                length = input.Length; modified = new(File.GetLastWriteTimeUtc(item.FullPath));
                                sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token));
                            }
                            catch { await input.DisposeAsync(); throw; }
                        }
                        catch (Exception ex) when (IsAccessFailure(ex))
                        { skipped.Add(item.RelativePath); BackupProgressContext.Report(BackupStage.ProcessingFiles, ++processed, scan.Files.Count); continue; }
                        await using (input)
                        {
                            input.Position = 0;
                            zip.PutNextEntry(Entry("data/" + item.RelativePath, length, password));
                            // Once an entry starts, any read/write error invalidates the archive.
                            await BackupProgressContext.CopyAsync(input, zip, BackupStage.ArchivingFile, length, token); zip.CloseEntry();
                            if (input.Length != length || File.GetLastWriteTimeUtc(item.FullPath) != modified.UtcDateTime)
                                throw new IOException("Файл изменился во время копирования: " + item.RelativePath);
                            files.Add(new(item.RelativePath, length, sha, modified));
                        }
                        BackupProgressContext.Report(BackupStage.ProcessingFiles, ++processed, scan.Files.Count);
                    }
                    var manifest = new FileBackupManifest
                    {
                        SnapshotId = id, FullSnapshotId = id, SourceIdentity = FileBackupManifest.IdentifySource(source, isFile), SourceIsFile = isFile,
                        Files = files, Directories = scan.Directories, ChangedFiles = files.Select(file => file.Path).ToArray(),
                        FormatVersion = skipped.Count > 0 ? 2 : 1, SkippedPaths = skipped.Count > 0 ? skipped.Order(StringComparer.Ordinal).ToArray() : null
                    };
                    manifest.Validate();
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, FileBackupManifest.Options);
                    zip.PutNextEntry(Entry(FileBackupManifest.ArchiveEntryName, bytes.Length, password));
                    await zip.WriteAsync(bytes, token); zip.CloseEntry(); zip.Finish();
                }
                await output.FlushAsync(token); output.Flush(true);
            }
            var after = ReadSource(source, isFile, token);
            bool Excluded(string path) => skipped.Any(skip => skip == "." || path.Equals(skip, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(skip + "/", StringComparison.OrdinalIgnoreCase));
            if (after.Skipped.Any(path => !Excluded(path))
                || !scan.Files.Where(file => !Excluded(file.RelativePath)).Select(file => file.RelativePath)
                    .SequenceEqual(after.Files.Where(file => !Excluded(file.RelativePath)).Select(file => file.RelativePath))
                || !scan.Directories.Where(path => !Excluded(path)).SequenceEqual(after.Directories.Where(path => !Excluded(path))))
                throw new IOException("Состав или доступность источника изменились во время копирования.");
            foreach (var file in files)
            {
                var info = new FileInfo(isFile ? source : FileBackupPaths.Resolve(source, file.Path));
                if (info.Length != file.Length || info.LastWriteTimeUtc != file.LastWriteUtc.UtcDateTime)
                    throw new IOException("Файл изменился во время копирования: " + file.Path);
            }
            BackupProgressContext.Report(BackupStage.Verifying);
            await FileBackupZip.VerifyAsync(temporary, password, token);
            token.ThrowIfCancellationRequested(); File.Move(temporary, destination); return destination;
        }
        finally { File.Delete(temporary); }
    }
    private static ZipEntry Entry(string name, long length, string? password) => new(name)
    { Size = length, AESKeySize = password is null ? 0 : 256, DateTime = DateTime.UtcNow };
}
