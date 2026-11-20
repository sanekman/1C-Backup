using System.Data.Common;
using System.Text;

namespace SPBackup.Core;

public sealed record OneCUserBase(string Name, string Directory, string PlatformVersion)
{
    public string DisplayName => string.IsNullOrEmpty(Directory) ? Name : Name + " · " + Directory;
}
public sealed record OneCUserBaseList(IReadOnlyList<OneCUserBase> Bases, int ServerBases, int UnreadableLists);

/// <summary>Reads launcher lists without modifying them or retaining connection credentials.</summary>
public static class OneCUserBases
{
    public static IReadOnlyList<OneCUserBase> Parse(string text)
    {
        var result = new List<OneCUserBase>();
        foreach (var section in Sections(text))
        {
            if (!section.Values.TryGetValue("Connect", out var connect)) continue;
            try
            {
                var values = new DbConnectionStringBuilder { ConnectionString = connect };
                if (!values.TryGetValue("File", out var path) || string.IsNullOrWhiteSpace(path.ToString())) continue;
                result.Add(new(section.Name, path.ToString()!, section.Values.GetValueOrDefault("Version", "").Trim()));
            }
            catch (ArgumentException) { }
        }
        return result;
    }
    public static OneCUserBaseList ReadCurrentUser() => Read(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "1C", "1CEStart"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "1C", "1CEStart"));

    public static OneCUserBaseList Read(string userLauncherDirectory, string? commonLauncherDirectory = null)
    {
        var lists = new List<string> { Path.Combine(userLauncherDirectory, "ibases.v8i") };
        var unreadable = 0; var servers = 0;
        foreach (var directory in new[] { userLauncherDirectory, commonLauncherDirectory }.Where(d => !string.IsNullOrWhiteSpace(d)))
        {
            var config = Path.Combine(directory!, "1CEStart.cfg");
            try
            {
                if (!File.Exists(config)) continue;
                foreach (var line in ReadText(config).Split('\n'))
                {
                    var split = line.IndexOf('=');
                    if (split < 0 || !line[..split].Trim().Equals("CommonInfoBases", StringComparison.OrdinalIgnoreCase)) continue;
                    var value = Environment.ExpandEnvironmentVariables(line[(split + 1)..].Trim().Trim('"'));
                    if (value.Length != 0) lists.Add(Path.GetFullPath(value, directory!));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { unreadable++; }
        }
        var bases = new List<OneCUserBase>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in lists.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(list)) continue;
                var text = ReadText(list);
                servers += Sections(text).Count(s => s.Values.TryGetValue("Connect", out var connect) && IsServer(connect));
                foreach (var entry in Parse(text))
                    if (seen.Add(entry.Directory.TrimEnd('\\', '/'))) bases.Add(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { unreadable++; }
        }
        return new(bases.OrderBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(), servers, unreadable);
    }
    private static bool IsServer(string connect)
    {
        try { var values = new DbConnectionStringBuilder { ConnectionString = connect }; return values.ContainsKey("Srvr"); }
        catch (ArgumentException) { return false; }
    }
    private static string ReadText(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > 4 * 1024 * 1024) throw new InvalidDataException("Список баз слишком большой.");
        using var memory = new MemoryStream(); file.CopyTo(memory);
        var bytes = memory.ToArray();
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) return Encoding.Unicode.GetString(bytes.AsSpan(2));
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) return Encoding.UTF8.GetString(bytes.AsSpan(3));
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251).GetString(bytes);
        }
    }
    private static IEnumerable<(string Name, Dictionary<string, string> Values)> Sections(string text)
    {
        string? name = null; var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.TrimStart('\uFEFF').Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (name is not null) yield return (name, values);
                name = line[1..^1]; values = new(StringComparer.OrdinalIgnoreCase); continue;
            }
            var separator = line.IndexOf('=');
            if (separator > 0 && name is not null) values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        if (name is not null) yield return (name, values);
    }
}
