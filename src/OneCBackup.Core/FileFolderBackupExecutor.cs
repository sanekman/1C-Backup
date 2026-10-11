using System.Security.Cryptography;
using System.Text.Json;
using ICSharpCode.SharpZipLib.Zip;

namespace OneCBackup.Core;

public sealed class FileFolderBackupExecutor(BackupCatalog catalog, FileBackupSnapshotStore snapshots,
    IArchivePasswordStore? passwords = null) : IBackupExecutor
{
    public async Task<string> CreateAsync(BackupJob job, string workDirectory, CancellationToken cancellationToken)
    {
        if (job.Kind != DatabaseKind.FilesAndFolders) throw new ArgumentException("Выберите копирование файлов и папок.");
        var errors = job.Validate(); if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
        var source = Path.GetFullPath(job.Source);
        var isFile = File.Exists(source);
        if (!isFile && !Directory.Exists(source)) throw new FileNotFoundException("Не найден исходный файл или папка.");
        if (!isFile)
        {
            FileBackupPaths.RequireOutside(source, workDirectory, "Временная папка должна находиться вне исходной папки.");
            FileBackupPaths.RequireOutside(source, snapshots.DirectoryPath, "Исходная папка содержит рабочий профиль программы. Выберите отдельную папку с данными.");
            foreach (var target in job.StorageJobs.Where(target => target.StorageKind != StorageKind.Ftp))
                FileBackupPaths.RequireOutside(source, target.DestinationDirectory, "Хранилища должны находиться вне исходной папки.");
        }
        var sourceIdentity = FileBackupManifest.IdentifySource(source, isFile);
        var password = await ArchivePasswords.ResolveAsync(job.ArchivePasswordKey, passwords, cancellationToken);
        FileBackupManifest? previous = null;
        if (job.FileBackupMode == FileBackupMode.Incremental)
        {
            try { previous = await snapshots.ReadAsync(job.Id, cancellationToken); }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { previous = null; }
            if (previous is not null && (previous.SourceIdentity != sourceIdentity || previous.Depth + 1 >= job.FullBackupEvery)) previous = null;
            if (previous is not null)
            {
                var records = await catalog.ReadAsync(cancellationToken);
                foreach (var target in job.StorageJobs)
                {
                    var newest = records.Where(record => record.JobId == job.Id && record.StorageIdentity == StorageAddress.Identity(target))
                        .OrderByDescending(record => record.CompletedUtc).ThenByDescending(record => record.Id).FirstOrDefault();
                    if (newest?.Backup.FileSnapshot != previous.GetSnapshot() || newest.Backup.ArchivePasswordKey != job.ArchivePasswordKey)
                    { previous = null; break; }
                    try { FileBackupChains.Resolve(newest, records); }
                    catch (InvalidDataException) { previous = null; break; }
                }
            }
        }
        var scan = FileBackupPaths.Scan(source, isFile, cancellationToken);
        var oldFiles = previous?.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, FileBackupEntry>(StringComparer.OrdinalIgnoreCase);
        var id = Guid.NewGuid();
        var destination = Path.Combine(Path.GetFullPath(workDirectory), $"{job.Id:N}-{id:N}.files-{(previous is null ? "full" : "incremental")}.zip");
        Directory.CreateDirectory(workDirectory);
        var temporary = destination + ".partial";
        var files = new List<FileBackupEntry>(); var changed = new List<string>();
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                using (var zip = new ZipOutputStream(output) { IsStreamOwner = false, Password = password, UseZip64 = UseZip64.Dynamic })
                {
                    zip.SetLevel(6);
                    foreach (var item in scan.Files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        FileBackupPaths.RejectReparse(item.FullPath);
                        await using var input = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                            FileOptions.Asynchronous | FileOptions.SequentialScan);
                        var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(item.FullPath));
                        var length = input.Length;
                        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken));
                        var descriptor = new FileBackupEntry(item.RelativePath, length, hash, modified);
                        files.Add(descriptor);
                        if (!oldFiles.TryGetValue(item.RelativePath, out var old) || old.Sha256 != hash || old.Length != length || old.Path != item.RelativePath)
                        {
                            changed.Add(item.RelativePath); input.Position = 0;
                            zip.PutNextEntry(Entry("data/" + item.RelativePath, length, password));
                            await input.CopyToAsync(zip, cancellationToken);
                            zip.CloseEntry();
                        }
                        if (input.Length != length || File.GetLastWriteTimeUtc(item.FullPath) != modified.UtcDateTime)
                            throw new IOException("Файл изменился во время копирования: " + item.RelativePath);
                    }
                    var currentPaths = files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var manifest = new FileBackupManifest { SnapshotId = id, FullSnapshotId = previous?.FullSnapshotId ?? id,
                        ParentSnapshotId = previous?.SnapshotId, SourceIdentity = sourceIdentity, SourceIsFile = isFile,
                        Depth = previous is null ? 0 : previous.Depth + 1, Files = files, Directories = scan.Directories,
                        ChangedFiles = changed, DeletedFiles = oldFiles.Keys.Where(path => !currentPaths.Contains(path)).Order(StringComparer.Ordinal).ToArray() };
                    manifest.Validate();
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, FileBackupManifest.Options);
                    zip.PutNextEntry(Entry(FileBackupManifest.ArchiveEntryName, bytes.Length, password));
                    await zip.WriteAsync(bytes, cancellationToken); zip.CloseEntry(); zip.Finish();
                }
                await output.FlushAsync(cancellationToken); output.Flush(true);
            }
            var after = FileBackupPaths.Scan(source, isFile, cancellationToken);
            if (!scan.Files.Select(file => file.RelativePath).SequenceEqual(after.Files.Select(file => file.RelativePath))
                || !scan.Directories.SequenceEqual(after.Directories))
                throw new IOException("Состав исходной папки изменился во время копирования. Повторите копирование при остановленных изменениях.");
            foreach (var file in files)
            {
                var path = isFile ? source : FileBackupPaths.Resolve(source, file.Path);
                var info = new FileInfo(path);
                if (info.Length != file.Length || info.LastWriteTimeUtc != file.LastWriteUtc.UtcDateTime)
                    throw new IOException("Файл изменился во время копирования: " + file.Path);
            }
            await FileBackupZip.VerifyAsync(temporary, password, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested(); File.Move(temporary, destination);
            return destination;
        }
        finally { File.Delete(temporary); }
    }

    private static ZipEntry Entry(string name, long length, string? password) => new(name)
    { Size = length, AESKeySize = password is null ? 0 : 256, DateTime = DateTime.UtcNow };
}

internal sealed record FileBackupSourceFile(string FullPath, string RelativePath);
internal sealed record FileBackupSourceScan(IReadOnlyList<FileBackupSourceFile> Files, IReadOnlyList<string> Directories);

internal static class FileBackupPaths
{
    internal static void RejectReparse(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Символические ссылки и точки соединения не копируются: " + path);
    }
    internal static void RequireOutside(string source, string candidate, string error)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)); var other = Path.GetFullPath(candidate);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (other.Equals(root, comparison) || other.StartsWith(prefix, comparison))
            throw new InvalidDataException(error);
    }
    internal static string Resolve(string root, string relative)
    {
        FileBackupManifest.ValidateRelativePath(relative);
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.GetFullPath(root);
        if (!Path.EndsInDirectorySeparator(prefix)) prefix += Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Путь выходит за каталог восстановления.");
        return path;
    }
    internal static FileBackupSourceScan Scan(string source, bool isFile, CancellationToken token)
    {
        RejectReparse(source);
        if (isFile)
        {
            var name = Path.GetFileName(source); FileBackupManifest.ValidateRelativePath(name);
            return new([new(source, name)], []);
        }
        var directories = new List<string>(); var files = new List<FileBackupSourceFile>();
        var pending = new Stack<string>(); pending.Push(source);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested(); RejectReparse(directory);
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                RejectReparse(child);
                var relative = Path.GetRelativePath(source, child).Replace(Path.DirectorySeparatorChar, '/');
                FileBackupManifest.ValidateRelativePath(relative);
                if (Directory.Exists(child)) { directories.Add(relative); pending.Push(child); }
                else files.Add(new(child, relative));
                if (files.Count > 100000 || directories.Count > 100000)
                    throw new InvalidDataException("Одна файловая копия поддерживает до 100000 файлов и 100000 папок.");
            }
        }
        return new(files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray(), directories.Order(StringComparer.Ordinal).ToArray());
    }
}
