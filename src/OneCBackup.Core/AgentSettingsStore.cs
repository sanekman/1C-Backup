using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneCBackup.Core;

internal sealed class AgentJsonFile<T>(string path) where T : class
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<T?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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
