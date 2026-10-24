using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneCBackup.Core;

/// <summary>Transfers a single job's settings, never credential values, catalog ownership or execution history.</summary>
public sealed class BackupJobTransfer
{
    public const int MaximumBytes = 1024 * 1024;
    private sealed record Document([property: JsonRequired] string Application,
        [property: JsonRequired] int FormatVersion, [property: JsonRequired] BackupJob Job);
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task ExportAsync(BackupJob job, string path, CancellationToken cancellationToken = default)
    {
        Validate(job);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Выберите полный путь файла настроек.");
        var target = Path.GetFullPath(path);
        if (!Directory.Exists(Path.GetDirectoryName(target))) throw new DirectoryNotFoundException("Выбранная папка не существует.");
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("Файл или папка уже существует. Выберите другое имя.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document("1C-Backup", 1, job), Options);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Настройки задания превышают допустимый размер (1 МБ).");
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".partial";
        var ownsTemporary = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemporary = true;
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: false);
        }
        finally { if (ownsTemporary && File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task<BackupJob> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = stream.Length;
        if (length is < 1 or > MaximumBytes) throw new InvalidDataException("Выберите файл настроек задания размером до 1 МБ.");
        var bytes = new byte[(int)length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (stream.ReadByte() != -1) throw new InvalidDataException("Файл настроек изменился во время чтения.");
        var document = JsonSerializer.Deserialize<Document>(bytes, Options)
            ?? throw new InvalidDataException("Файл настроек содержит null.");
        if (document.Application != "1C-Backup" || document.FormatVersion != 1)
            throw new InvalidDataException("Неизвестный формат настроек задания.");
        Validate(document.Job);
        // Imported settings always create a new disabled job; no incoming ID may replace an existing job.
        return document.Job with { Id = Guid.NewGuid(), Enabled = false };
    }

    private static void Validate(BackupJob job)
    {
        if (job is null) throw new InvalidDataException("В файле нет задания.");
        var errors = job.Validate();
        if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
    }
}
