using Microsoft.AspNetCore.Builder;
using System.Text.Json;

namespace OneCBackup.Remote;

/// <summary>Same relay runtime for Windows GUI/agent and Linux CLI. Owns a separate lease from backup execution.</summary>
public sealed class EmbeddedHub(string directory)
{
    public async Task RunAsync(Action<string?> warning, CancellationToken token)
    {
        WebApplication? app = null; FileStream? lease = null; string? active = null;
        async Task Stop()
        {
            if (app is not null) { await app.DisposeAsync(); app = null; }
            lease?.Dispose(); lease = null; active = null;
        }
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var path = Path.Combine(directory, "remote-hub.json");
                    var options = File.Exists(path) ? await RemoteConfiguration.ReadAsync<HubOptions>(path, token) : new HubOptions();
                    options.Validate(); var identity = JsonSerializer.Serialize(options);
                    if (!options.Enabled) { await Stop(); warning(null); }
                    else if (identity != active)
                    {
                        await Stop(); Directory.CreateDirectory(directory);
                        try { lease = new FileStream(Path.Combine(directory, "remote-hub.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                        catch (IOException) { warning(null); }
                        if (lease is not null) { app = RemoteHub.Build(options); await app.StartAsync(token); active = identity; warning(null); }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch { await Stop(); warning("Публичный узел не запущен. Проверьте настройки и свободный локальный порт; копирование продолжается."); }
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { await Stop(); }
    }
}
