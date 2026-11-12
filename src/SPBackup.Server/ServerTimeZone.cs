using System.Globalization;
using System.Text.Json;

namespace SPBackup.Server;

public sealed record ServerTimeZoneOption(string Id, string Label);

/// <summary>Server-wide display preference. All stored instants and API contracts remain UTC.</summary>
public sealed class ServerTimeZone
{
    private sealed record Preference(int FormatVersion, string TimeZoneId);
    private sealed record State(string Id, TimeZoneInfo Zone, bool Warning);
    private static readonly IReadOnlyDictionary<string, string> Common = new Dictionary<string, string>
    {
        ["UTC"] = "UTC — всемирное время", ["Europe/Kaliningrad"] = "Калининград", ["Europe/Moscow"] = "Москва",
        ["Europe/Samara"] = "Самара", ["Asia/Yekaterinburg"] = "Екатеринбург", ["Asia/Omsk"] = "Омск",
        ["Asia/Krasnoyarsk"] = "Красноярск", ["Asia/Irkutsk"] = "Иркутск", ["Asia/Chita"] = "Чита",
        ["Asia/Vladivostok"] = "Владивосток", ["Asia/Magadan"] = "Магадан", ["Asia/Kamchatka"] = "Камчатка"
    };
    private static readonly Lazy<IReadOnlyDictionary<string, TimeZoneInfo>> Available = new(() =>
        TimeZoneInfo.GetSystemTimeZones().ToDictionary(z => z.Id, StringComparer.OrdinalIgnoreCase));
    private readonly string file;
    private readonly SemaphoreSlim gate = new(1, 1);
    private State state;
    public ServerTimeZone(ServerOptions options)
    {
        file = Path.Combine(options.DataDirectory, "panel-time-zone.json");
        state = new("UTC", TimeZoneInfo.Utc, false);
        try
        {
            if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) throw new IOException();
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > 4096) throw new InvalidDataException();
            var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
            var setting = JsonSerializer.Deserialize<Preference>(bytes) ?? throw new InvalidDataException();
            if (setting.FormatVersion != 1) throw new InvalidDataException();
            var resolved = Resolve(setting.TimeZoneId);
            state = new(resolved.Id, resolved.Zone, false);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ServerRequestException)
        { state = state with { Warning = true }; }
    }
    public ServerClock Snapshot()
    {
        var current = Volatile.Read(ref state);
        return new(current.Id, current.Zone, current.Warning);
    }
    public static (string Id, TimeZoneInfo Zone) Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(char.IsControl)) throw new ServerRequestException(400, "invalid_time_zone");
        if (id is "UTC" or "Etc/UTC" or "Etc/GMT") return ("UTC", TimeZoneInfo.Utc);
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var mapped)) id = mapped;
        else if (Available.Value.TryGetValue(id, out var native)) return (native.Id, native);
        if (id is "UTC" or "Etc/UTC" or "Etc/GMT") return ("UTC", TimeZoneInfo.Utc);
        // Do not pass absolute paths, traversal or arbitrary filenames to the platform tzdata loader.
        if (id.StartsWith('/') || id.Contains("..", StringComparison.Ordinal)
            || !id.Contains('/') || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '/' and not '_' and not '-' and not '+'))
            throw new ServerRequestException(400, "invalid_time_zone");
        try { return (id, TimeZoneInfo.FindSystemTimeZoneById(id)); }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new ServerRequestException(400, "invalid_time_zone"); }
    }
    public static string Label(string id)
    {
        if (Common.TryGetValue(id, out var name)) return id == "UTC" ? name : name + " (" + id + ")";
        return Available.Value.TryGetValue(id, out var zone) && zone.DisplayName != id ? zone.DisplayName + " (" + id + ")" : id;
    }
    public static IReadOnlyList<ServerTimeZoneOption> Options(string selected)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { "UTC", selected };
        foreach (var zone in Available.Value.Values)
        {
            try { ids.Add(Resolve(zone.Id).Id); }
            catch (ServerRequestException) { }
        }
        var common = new List<ServerTimeZoneOption>();
        foreach (var id in Common.Keys)
        {
            try { _ = Resolve(id); common.Add(new(id, Label(id))); ids.Remove(id); }
            catch (ServerRequestException) { }
        }
        return common.Concat(ids.OrderBy(id => id, StringComparer.Ordinal).Select(id => new ServerTimeZoneOption(id, Label(id)))).ToArray();
    }
    public async Task SaveAsync(string id, CancellationToken token = default)
    {
        var next = Resolve(id);
        await gate.WaitAsync(token);
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (File.Exists(file) && File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) throw new IOException();
            temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Preference(1, next.Id));
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var output = new FileStream(temporary, options))
            {
                await output.WriteAsync(bytes, token); await output.FlushAsync(token); output.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, file, overwrite: true);
            Volatile.Write(ref state, new State(next.Id, next.Zone, false));
        }
        finally
        {
            try { if (temporary is not null) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            gate.Release();
        }
    }
}

/// <summary>One snapshot per HTTP request prevents mixing zones during a concurrent preference change.</summary>
public sealed class ServerClock(string id, TimeZoneInfo zone, bool loadWarning = false)
{
    public string ZoneId { get; } = id;
    public string ZoneLabel => ServerTimeZone.Label(ZoneId);
    public bool LoadWarning { get; } = loadWarning;
    public string Time(DateTimeOffset? value) => value is { } instant
        ? TimeZoneInfo.ConvertTime(instant, zone).ToString("dd.MM.yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture)
        : "Ещё не получена";
}
