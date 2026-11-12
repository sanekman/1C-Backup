using System.Text.Json;
using System.Text.Json.Serialization;

namespace SPBackup.Core;

internal sealed class AgentJsonFile<T>(string path) where T : class
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public Task<T?> ReadAsync(CancellationToken cancellationToken) => ReadAsync(cancellationToken, null);
    internal Task<T?> ReadBoundedAsync(long maximumBytes, CancellationToken cancellationToken) => ReadAsync(cancellationToken, maximumBytes);
    private async Task<T?> ReadAsync(CancellationToken cancellationToken, long? maximumBytes)
    {
        await using var stream = await AtomicProfileFile.OpenReadAsync(path, cancellationToken);
        if (stream is null) return null;
        if (maximumBytes is { } limit)
        {
            // Validate the opened snapshot, not a name briefly replaced by another process.
            if (stream.Length > limit) throw new InvalidDataException("Файл команды или результата слишком велик.");
            FileBackupPaths.RejectReparse(Path.GetDirectoryName(Path.GetFullPath(path))!);
            try { FileBackupPaths.RejectReparse(path); }
            catch (FileNotFoundException) { /* Replacement hid the name; the opened snapshot is still valid. */ }
            if (File.GetAttributes(stream.SafeFileHandle).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Чтение команды или результата через ссылку запрещено.");
        }
        return await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken)
            ?? throw new InvalidDataException("Файл конфигурации агента содержит null.");
    }

    public async Task WriteAsync(T value, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            AtomicProfileFile.Publish(temporary, fullPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class AgentSettingsStore(string path)
{
    private readonly AgentJsonFile<AgentSettings> file = new(path);
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<AgentSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var settings = await file.ReadAsync(cancellationToken);
            if (settings is null)
            {
                settings = new AgentSettings();
                await file.WriteAsync(settings, cancellationToken);
            }
            Validate(settings);
            return settings;
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(AgentSettings settings, CancellationToken cancellationToken = default)
    {
        Validate(settings);
        await gate.WaitAsync(cancellationToken);
        try { await file.WriteAsync(settings, cancellationToken); }
        finally { gate.Release(); }
    }

    private static void Validate(AgentSettings settings)
    {
        var errors = settings.Validate();
        if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
    }
}
