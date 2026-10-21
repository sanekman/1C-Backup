using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneCBackup.Core;

public sealed record FileBackupSnapshot(Guid SnapshotId, Guid FullSnapshotId, Guid? ParentSnapshotId,
    string SourceIdentity, bool SourceIsFile, int Depth, string ManifestSha256)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsIncomplete { get; init; }
    public bool IsFull => ParentSnapshotId is null;
    public void Validate()
    {
        if (SnapshotId == Guid.Empty || FullSnapshotId == Guid.Empty || !FileBackupManifest.IsHash(SourceIdentity)
            || !FileBackupManifest.IsHash(ManifestSha256) || Depth is < 0 or >= 365
            || (IsFull ? Depth != 0 || FullSnapshotId != SnapshotId
                : ParentSnapshotId == Guid.Empty || ParentSnapshotId == SnapshotId || FullSnapshotId == SnapshotId || Depth == 0))
            throw new InvalidDataException("Некорректные сведения о цепочке файловых копий.");
    }
}

public sealed record FileBackupEntry(string Path, long Length, string Sha256, DateTimeOffset LastWriteUtc)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WebDavETag { get; init; }
}

public sealed record FileBackupManifest
{
    public const int MaximumItems = 1_000_000;
    public const int MaximumBytes = 512 * 1024 * 1024;
    public const string ArchiveEntryName = "onec-backup-manifest.json";
    internal static readonly JsonSerializerOptions Options = new()
    { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    [JsonRequired] public string Application { get; init; } = "1C-Backup";
    [JsonRequired] public int FormatVersion { get; init; } = 1;
    [JsonRequired] public Guid SnapshotId { get; init; }
    [JsonRequired] public Guid FullSnapshotId { get; init; }
    [JsonRequired] public Guid? ParentSnapshotId { get; init; }
    [JsonRequired] public string SourceIdentity { get; init; } = "";
    [JsonRequired] public bool SourceIsFile { get; init; }
    [JsonRequired] public int Depth { get; init; }
    [JsonRequired] public IReadOnlyList<FileBackupEntry> Files { get; init; } = [];
    [JsonRequired] public IReadOnlyList<string> Directories { get; init; } = [];
    [JsonRequired] public IReadOnlyList<string> ChangedFiles { get; init; } = [];
    [JsonRequired] public IReadOnlyList<string> DeletedFiles { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? SkippedPaths { get; init; }

    internal static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    internal static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Contains('\\')
            || path.Any(char.IsControl) || path.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0
            || path.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.TrimEnd(' ', '.') != part || IsWindowsDevice(part)))
            throw new InvalidDataException("Некорректный относительный путь в файловой копии.");
    }
    private static bool IsWindowsDevice(string part)
    {
        var name = part.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal))
                && (name[3] is >= '1' and <= '9' or '¹' or '²' or '³');
    }
    public void Validate()
    {
        if (Application != "1C-Backup" || FormatVersion is not (1 or 2 or 3) || Files is null || Directories is null
            || ChangedFiles is null || DeletedFiles is null || Files.Count > MaximumItems || Directories.Count > MaximumItems
            || ChangedFiles.Count > MaximumItems || DeletedFiles.Count > MaximumItems)
            throw new InvalidDataException("Некорректный формат или слишком большой индекс файловой копии.");
        var incomplete = SkippedPaths?.Count > 0;
        if ((FormatVersion >= 2) != incomplete || SkippedPaths?.Count > MaximumItems || FormatVersion == 2 && ParentSnapshotId is not null)
            throw new InvalidDataException("Некорректная отметка неполной копии.");
        var skipped = new HashSet<string>(SkippedPaths ?? [], StringComparer.OrdinalIgnoreCase);
        if (SkippedPaths is not null)
        {
            if (skipped.Count != SkippedPaths.Count)
                throw new InvalidDataException("Повторяющиеся пропущенные пути.");
            foreach (var path in SkippedPaths)
            {
                if (path != ".") ValidateRelativePath(path);
            }
        }
        var snapshot = GetSnapshot(); snapshot.Validate();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            if (file is null || file.Length < 0 || !IsHash(file.Sha256) || file.LastWriteUtc == default || file.LastWriteUtc.Offset != TimeSpan.Zero)
                throw new InvalidDataException("Некорректный файл в индексе копии.");
            if (file.WebDavETag is not null && (file.WebDavETag.Length > 1024 || file.WebDavETag.Any(char.IsControl)))
                throw new InvalidDataException("Некорректный ETag файла WebDAV.");
            ValidateRelativePath(file.Path);
            // Format 3 may retain an older version from the parent, or contain
            // readable children of a directory whose enumeration was incomplete.
            if (FormatVersion != 3 && IsSkipped(file.Path, skipped))
                throw new InvalidDataException("Пропущенный путь содержит скопированные файлы.");
            if (!names.Add(file.Path)) throw new InvalidDataException("Повторяющийся путь в индексе копии.");
        }
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directories)
        {
            ValidateRelativePath(directory);
            if (!directories.Add(directory) || names.Contains(directory)) throw new InvalidDataException("Повторяющаяся папка в индексе копии.");
        }
        foreach (var path in names.Concat(directories))
        {
            var slash = path.LastIndexOf('/');
            if (slash >= 0 && !directories.Contains(path[..slash])) throw new InvalidDataException("В индексе отсутствует родительская папка.");
        }
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in ChangedFiles)
        {
            ValidateRelativePath(path);
            if (!names.Contains(path) || !changed.Add(path) || skipped.Contains(path)) throw new InvalidDataException("Некорректный список изменённых файлов.");
        }
        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in DeletedFiles)
        {
            ValidateRelativePath(path);
            if (names.Contains(path) || !deleted.Add(path) || IsSkipped(path, skipped)) throw new InvalidDataException("Некорректный список удалённых файлов.");
        }
        if (ParentSnapshotId is null && (changed.Count != Files.Count || deleted.Count != 0)
            || SourceIsFile && (Files.Count != 1 && !(incomplete && Files.Count == 0) || Directories.Count != 0))
            throw new InvalidDataException("Полная копия или индекс одиночного файла некорректны.");
    }
    public FileBackupSnapshot GetSnapshot()
    {
        using var hash = SHA256.Create();
        using var hashed = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write, leaveOpen: true);
        using var bounded = new FileManifestStream(hashed);
        JsonSerializer.Serialize(bounded, this, Options);
        hashed.FlushFinalBlock();
        return new(SnapshotId, FullSnapshotId, ParentSnapshotId, SourceIdentity, SourceIsFile, Depth,
            Convert.ToHexStringLower(hash.Hash!)) { IsIncomplete = SkippedPaths?.Count > 0 };
    }
    internal static bool IsSkipped(string path, ISet<string> skipped)
    {
        if (skipped.Contains(".")) return true;
        while (true)
        {
            if (skipped.Contains(path)) return true;
            var slash = path.LastIndexOf('/');
            if (slash < 0) return false;
            path = path[..slash];
        }
    }
    internal static string IdentifySource(string source, bool isFile)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes((isFile ? "file|" : "directory|") + path)));
    }
    internal static async Task<FileBackupManifest> ReadAsync(Stream stream, CancellationToken token)
    {
        using var bounded = new FileManifestStream(stream);
        var result = await JsonSerializer.DeserializeAsync<FileBackupManifest>(bounded, Options, token)
            ?? throw new InvalidDataException("Пустой индекс файловой копии.");
        result.Validate(); return result;
    }
}

public sealed class FileBackupSnapshotStore(string directory)
{
    internal string DirectoryPath => directory;
    public async Task<FileBackupManifest?> ReadAsync(Guid jobId, CancellationToken token = default)
    {
        var path = Path.Combine(directory, jobId.ToString("N") + ".json");
        if (!File.Exists(path)) return null;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return await FileBackupManifest.ReadAsync(input, token);
    }
    public async Task SaveAsync(Guid jobId, FileBackupManifest manifest, CancellationToken token = default)
    {
        manifest.Validate(); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, jobId.ToString("N") + ".json"); var stage = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var bounded = new FileManifestStream(output);
                await JsonSerializer.SerializeAsync(bounded, manifest, FileBackupManifest.Options, token);
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested(); AtomicProfileFile.Publish(stage, path);
        }
        finally { File.Delete(stage); }
    }
}

public static class FileBackupChains
{
    public static IReadOnlyList<BackupRecord> Resolve(BackupRecord selected, IEnumerable<BackupRecord> records)
    {
        var first = selected.Backup.FileSnapshot ?? throw new InvalidDataException("У копии отсутствуют сведения о файловой цепочке.");
        first.Validate();
        var candidates = records.Where(record => record.JobId == selected.JobId && record.StorageIdentity == selected.StorageIdentity
            && record.Backup.FileSnapshot is not null).ToArray();
        var result = new List<BackupRecord>(); var current = selected; var visited = new HashSet<Guid>();
        while (true)
        {
            var snapshot = current.Backup.FileSnapshot!; snapshot.Validate();
            if (!visited.Add(snapshot.SnapshotId) || snapshot.SourceIdentity != first.SourceIdentity
                || snapshot.FullSnapshotId != first.FullSnapshotId || snapshot.SourceIsFile != first.SourceIsFile
                || current.Backup.ArchivePasswordKey != selected.Backup.ArchivePasswordKey)
                throw new InvalidDataException("Файловая цепочка противоречива или содержит цикл.");
            result.Add(current);
            if (snapshot.ParentSnapshotId is not { } parent) break;
            var matches = candidates.Where(record => record.Backup.FileSnapshot!.SnapshotId == parent).ToArray();
            if (matches.Length != 1 || matches[0].Backup.FileSnapshot!.Depth != snapshot.Depth - 1)
                throw new InvalidDataException("Отсутствует или неоднозначна предыдущая копия файловой цепочки. Восстановление требует полную основу и все последующие изменения.");
            current = matches[0];
        }
        result.Reverse(); return result;
    }
}
