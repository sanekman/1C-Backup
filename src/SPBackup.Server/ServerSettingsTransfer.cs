using System.Text.Json;

namespace SPBackup.Server;

internal sealed class ServerSettingsTransfer(ServerStore store, ServerOptions options, ServerTimeZone zones) : IHostedService
{
    internal const int MaximumBytes = 4 * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task StartAsync(CancellationToken token)
    {
        if (await store.ImportedPanelAsync(token) is { } panel) Apply(panel);
    }

    public Task StopAsync(CancellationToken token) => Task.CompletedTask;

    internal async Task<byte[]> ExportAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var settings = await store.ExportPortableAsync(new(options.DisplayName, zones.Snapshot().ZoneId), token);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, FileConfigurationContract.Json);
            if (bytes.Length > MaximumBytes) throw new ServerRequestException(413, "portable_settings_too_large");
            return bytes;
        }
        finally { gate.Release(); }
    }

    internal async Task ImportAsync(Guid administrator, Guid stamp, IFormFile file, CancellationToken token)
    {
        if (file.Length is < 1 or > MaximumBytes) throw new ServerRequestException(413, "portable_settings_too_large");
        PortableServerSettings settings;
        try
        {
            await using var input = file.OpenReadStream();
            settings = await JsonSerializer.DeserializeAsync<PortableServerSettings>(input, FileConfigurationContract.Json, token)
                ?? throw new JsonException();
        }
        catch (JsonException) { throw new ServerRequestException(400, "invalid_portable_settings"); }
        await gate.WaitAsync(token);
        try
        {
            await store.ImportPortableAsync(administrator, stamp, settings, token);
            Apply(settings.Panel);
        }
        finally { gate.Release(); }
    }

    internal async Task SaveZoneAsync(string id, CancellationToken token)
    {
        var zone = ServerTimeZone.Resolve(id);
        await gate.WaitAsync(token);
        try
        {
            if (await store.SaveImportedZoneAsync(zone.Id, token)) zones.ApplyImported(zone.Id);
            else await zones.SaveAsync(zone.Id, token);
        }
        finally { gate.Release(); }
    }

    private void Apply(PortablePanelSettings panel)
    {
        zones.ApplyImported(panel.TimeZoneId);
        options.ApplyImportedDisplayName(panel.DisplayName);
    }
}
