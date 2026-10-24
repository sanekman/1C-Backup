namespace OneCBackup.Core;

public sealed class FileFolderBackupExecutor(BackupCatalog catalog, FileBackupSnapshotStore snapshots,
    IArchivePasswordStore? passwords = null, Func<string, Stream>? openSource = null,
    Func<string, IEnumerable<string>>? enumerateSource = null, TimeProvider? timeProvider = null) : IBackupExecutor
{
    public async Task<string> CreateAsync(BackupJob job, string workDirectory, CancellationToken cancellationToken)
    {
        if (job.Kind != DatabaseKind.FilesAndFolders) throw new ArgumentException("Выберите копирование файлов и папок.");
        var errors = job.Validate(); if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
        var source = Path.GetFullPath(job.Source);
        var isFile = File.Exists(source);
        if (!isFile && !Directory.Exists(source)) throw new FileNotFoundException("Не найден исходный файл или папка.");
        if (!isFile)
            FileBackupPaths.RequireOutside(source, snapshots.DirectoryPath, "Исходная папка содержит рабочий профиль программы. Выберите отдельную папку с данными.");
        var previous = await FileBackupBases.ReadAsync(job, FileBackupManifest.IdentifySource(source, isFile),
            catalog, snapshots, cancellationToken, timeProvider);
        return await new AccessibleFilesBackupExecutor(this, passwords, enumerateSource, openSource)
            .CreateSnapshotAsync(job, workDirectory, cancellationToken, previous);
    }
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
                token.ThrowIfCancellationRequested();
                RejectReparse(child);
                var relative = Path.GetRelativePath(source, child).Replace(Path.DirectorySeparatorChar, '/');
                FileBackupManifest.ValidateRelativePath(relative);
                if (Directory.Exists(child)) { directories.Add(relative); pending.Push(child); }
                else files.Add(new(child, relative));
                if (files.Count > FileBackupManifest.MaximumItems || directories.Count > FileBackupManifest.MaximumItems)
                    throw new InvalidDataException("Одна файловая копия поддерживает до 1000000 файлов и 1000000 папок.");
            }
        }
        return new(files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray(), directories.Order(StringComparer.Ordinal).ToArray());
    }
}
