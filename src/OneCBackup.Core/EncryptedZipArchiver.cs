using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.Zip;

namespace OneCBackup.Core;

/// <summary>Standard WinZip AES-256 ZIP. Only verified encrypted bytes reach the destination.</summary>
internal static class EncryptedZipArchiver
{
    internal static async Task<string> CreateAsync(Stream input, string name, string destination, string password,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ArchivePasswords.Validate(password);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            long length = 0;
            byte[] digest;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var zip = new ZipOutputStream(output) { IsStreamOwner = false, Password = password, UseZip64 = UseZip64.Dynamic };
                var finished = false;
                try
                {
                    zip.SetLevel(6);
                    var entry = new ZipEntry(name) { AESKeySize = 256, IsUnicodeText = true };
                    if (input.CanSeek) entry.Size = input.Length - input.Position;
                    await zip.PutNextEntryAsync(entry, cancellationToken);
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
                    {
                        await zip.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        hash.AppendData(buffer, 0, read);
                        length += read;
                        progress?.Report(length);
                    }
                    await zip.FinishAsync(cancellationToken);
                    finished = true;
                }
                finally
                {
                    // Dispose also finalizes an entry; do not let it replace cancellation / I/O failures.
                    try { zip.Dispose(); }
                    catch (Exception) when (!finished) { }
                }
                digest = hash.GetHashAndReset();
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            // Decrypt and consume through the authentication code before publishing.
            using (var zip = new ZipFile(temporary) { Password = password })
            {
                var entry = zip[0];
                if (zip.Count != 1 || entry.Name != name || entry.Size != length || !entry.IsCrypted || entry.AESKeySize != 256)
                    throw new InvalidDataException("Структура зашифрованного ZIP не соответствует копии.");
                await using var content = zip.GetInputStream(entry);
                var verified = await SHA256.HashDataAsync(content, cancellationToken);
                if (!CryptographicOperations.FixedTimeEquals(digest, verified))
                    throw new InvalidDataException("Проверка зашифрованного ZIP не пройдена.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
