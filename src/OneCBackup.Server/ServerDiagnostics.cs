using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OneCBackup.Server;

/// <summary>Support export contains fixed health fields and whitelisted event metadata, never raw files.</summary>
public sealed class ServerDiagnostics(ServerOptions options, ServerStore store, IServiceProvider services)
{
    private sealed record SafeEvent(DateTimeOffset Utc, string Event, int? HResult);
    public async Task<byte[]> CreateAsync(CancellationToken token = default)
    {
        long? free = null;
        try { free = new DriveInfo(Path.GetPathRoot(options.DataDirectory)!).AvailableFreeSpace; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        var certificate = services.GetService<X509Certificate2>();
        var summary = new
        {
            FormatVersion = 1, GeneratedUtc = DateTimeOffset.UtcNow,
            Version = typeof(ServerDiagnostics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            Runtime = Environment.Version.ToString(), Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            OperatingSystem = RuntimeInformation.OSDescription,
            SupportedSchemaVersion = ServerStore.SchemaVersion, DatabaseReady = await store.ReadyAsync(token),
            FreeDataDiskBytes = free, CertificateNotBeforeUtc = certificate?.NotBefore.ToUniversalTime(),
            CertificateNotAfterUtc = certificate?.NotAfter.ToUniversalTime(), CertificateManagedExternally = certificate is null
        };
        var events = new List<SafeEvent>(); var skipped = 0; long remaining = 8 * 1024 * 1024;
        var directory = Path.Combine(options.DataDirectory, "logs");
        try
        {
            if (Directory.Exists(directory) && !File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            foreach (var file in Directory.EnumerateFiles(directory).Where(path => Regex.IsMatch(Path.GetFileName(path), @"^server-[0-9]{8}-[a-f0-9]{32}-[0-9]{6}\.jsonl$"))
                .Select(path => new FileInfo(path)).OrderByDescending(file => file.LastWriteTimeUtc).Take(20))
            {
                token.ThrowIfCancellationRequested();
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length > 2 * 1024 * 1024 || file.Length > remaining) { skipped++; continue; }
                try
                {
                    using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    // A logger may append concurrently; read only a bounded snapshot of this file.
                    var length = (int)Math.Min(input.Length, Math.Min(remaining, 2 * 1024 * 1024));
                    remaining -= length;
                    var bytes = new byte[length]; var read = 0;
                    while (read < length) { var n = await input.ReadAsync(bytes.AsMemory(read), token); if (n == 0) break; read += n; }
                    foreach (var line in System.Text.Encoding.UTF8.GetString(bytes, 0, read).Split('\n'))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        if (line.Length > 65536) { skipped++; continue; }
                        try
                        {
                            using var document = JsonDocument.Parse(line); var item = document.RootElement;
                            if (!item.TryGetProperty("Event", out var kind) || kind.ValueKind != JsonValueKind.String
                                || !Enum.TryParse<ServerEvent>(kind.GetString(), out var value) || !Enum.IsDefined(value)
                                || !item.TryGetProperty("Utc", out var utc) || !utc.TryGetDateTimeOffset(out var time)) { skipped++; continue; }
                            int? hresult = item.TryGetProperty("HResult", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var number) ? number : null;
                            events.Add(new(time.ToUniversalTime(), value.ToString(), hresult));
                            // Bound memory even for very small valid log lines.
                            if (events.Count > 1000) events = events.OrderByDescending(e => e.Utc).Take(200).ToList();
                        }
                        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException) { skipped++; }
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { skipped++; }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { skipped++; }
        token.ThrowIfCancellationRequested();
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            await JsonAsync(zip, "server.json", summary, token);
            await JsonAsync(zip, "events.json", new { Events = events.OrderByDescending(e => e.Utc).Take(200), SkippedRecords = skipped }, token);
            using var writer = new StreamWriter(zip.CreateEntry("README.txt").Open());
            await writer.WriteAsync("Диагностика 1C-Backup Server. Содержит состояние сервера и до 200 последних событий.\nНе содержит базу данных, настройки, адреса, имена компьютеров/пользователей, пароли, токены, сертификаты или исходные файлы журналов.\nЭто диагностика, а не резервная копия сервера. Передайте ZIP поддержке вместе с временем и описанием проблемы.\n");
        }
        return output.ToArray();
    }
    private static async Task JsonAsync(ZipArchive zip, string name, object value, CancellationToken token)
    {
        await using var stream = zip.CreateEntry(name).Open();
        await JsonSerializer.SerializeAsync(stream, value, new JsonSerializerOptions { WriteIndented = true }, token);
    }
}
