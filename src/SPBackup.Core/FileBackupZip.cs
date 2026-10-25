using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.Zip;

namespace SPBackup.Core;

internal static class FileBackupZip
{
    internal static async Task WriteManifestAsync(ZipOutputStream zip, FileBackupManifest manifest, string? password, CancellationToken token)
    {
        // Measure using a bounded sink, then write directly into the ZIP entry.
        using var measured = new FileManifestStream(Stream.Null);
        await System.Text.Json.JsonSerializer.SerializeAsync(measured, manifest, FileBackupManifest.Options, token);
        zip.PutNextEntry(new ZipEntry(FileBackupManifest.ArchiveEntryName)
        { Size = measured.Length, AESKeySize = password is null ? 0 : 256, DateTime = DateTime.UtcNow });
        using var bounded = new FileManifestStream(zip);
        await System.Text.Json.JsonSerializer.SerializeAsync(bounded, manifest, FileBackupManifest.Options, token);
        zip.CloseEntry();
    }
    internal static async Task<FileBackupManifest> ReadManifestAsync(string path, string? password, CancellationToken token)
    {
        using var zip = new ZipFile(path) { Password = password };
        return await ReadManifestAsync(zip, password, token);
    }
    private static async Task<FileBackupManifest> ReadManifestAsync(ZipFile zip, string? password, CancellationToken token)
    {
        var entry = zip.GetEntry(FileBackupManifest.ArchiveEntryName)
            ?? throw new InvalidDataException("В ZIP отсутствует индекс файловой копии.");
        if (entry.Size < 1 || entry.Size > FileBackupManifest.MaximumBytes)
            throw new InvalidDataException("Некорректный размер индекса файловой копии.");
        await using var input = zip.GetInputStream(entry);
        var manifest = await FileBackupManifest.ReadAsync(input, token);
        ValidateStructure(zip, manifest, password); return manifest;
    }
    internal static void ValidateStructure(ZipFile zip, FileBackupManifest manifest, string? password)
    {
        var expected = manifest.ChangedFiles.Select(path => "data/" + path).Append(FileBackupManifest.ArchiveEntryName)
            .ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipEntry entry in zip)
        {
            if (!entry.IsFile || !expected.Contains(entry.Name) || !seen.Add(entry.Name)
                || (password is null ? entry.IsCrypted : !entry.IsCrypted || entry.AESKeySize != 256))
                throw new InvalidDataException("ZIP содержит неожиданные, повторяющиеся или неправильно защищённые записи.");
        }
        if (seen.Count != expected.Count) throw new InvalidDataException("В ZIP отсутствует изменённый файл.");
    }
    internal static async Task VerifyAsync(string path, string? password, CancellationToken token)
    {
        using var zip = new ZipFile(path) { Password = password };
        var manifest = await ReadManifestAsync(zip, password, token);
        var files = manifest.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        foreach (var name in manifest.ChangedFiles)
        {
            await using var input = zip.GetInputStream(zip.GetEntry("data/" + name));
            await CopyVerifiedAsync(input, Stream.Null, files[name], token);
        }
    }
    internal static async Task CopyVerifiedAsync(Stream input, Stream output, FileBackupEntry expected, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920]; long bytes = 0; int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            bytes = checked(bytes + count);
            if (bytes > expected.Length) throw new InvalidDataException("Размер файла в архиве не совпадает с индексом: " + expected.Path);
            hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        if (bytes != expected.Length || !Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SHA-256 или размер файла не совпадает с индексом: " + expected.Path);
    }
}
