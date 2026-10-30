using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SPBackup.Server;

// Limited to Windows file jobs. No credentials, executables, SQL or arbitrary JSON.
public sealed record ManagedFileJob
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string Kind { get; init; } = "FilesAndFolders";
    public required string Source { get; init; }
    public required string DestinationDirectory { get; init; }
    public string? TemporaryDirectory { get; init; }
    public string StorageKind { get; init; } = "LocalDirectory";
    public bool Enabled { get; init; }
    public string ScheduleKind { get; init; } = "Manual";
    public TimeOnly DailyAt { get; init; } = new(23, 0);
    public IReadOnlyList<TimeOnly>? AdditionalDailyTimes { get; init; }
    public string DaysOfWeek { get; init; } = "Monday";
    public string TimeZoneId { get; init; } = "UTC";
    public int KeepLast { get; init; } = 7;
    public string FileBackupMode { get; init; } = "Full";
    public int FullBackupEvery { get; init; } = 7;
    public DayOfWeek? FullBackupDayOfWeek { get; init; }
    public string OneCMethod { get; init; } = "FileArchive";
    public bool AllowOpenOneCDatabase { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ManagedFileDestination>? AdditionalDestinations { get; init; }
}
public sealed record ManagedFileDestination
{
    public required string StorageKind { get; init; }
    public required string DestinationDirectory { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? KeepLast { get; init; }
}
public sealed record PublishFileConfiguration(Guid AgentId, int ProtocolVersion, IReadOnlyList<ManagedFileJob> Jobs);
public sealed record ManagedFileConfiguration(Guid AgentId, long Revision, IReadOnlyList<ManagedFileJob> Jobs, int ProtocolVersion = 1);

internal static class FileConfigurationContract
{
    internal const int MaxBytes = 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true,
        Converters = { new SingleDayConverter() }
    };
    private sealed class SingleDayConverter : JsonConverter<DayOfWeek>
    {
        public override DayOfWeek Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } name
                && Enum.GetNames<DayOfWeek>().Contains(name) && Enum.TryParse<DayOfWeek>(name, out var day)) return day;
            throw new JsonException();
        }
        public override void Write(Utf8JsonWriter writer, DayOfWeek value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }
    internal static long ExpectedRevision(HttpContext context)
    {
        var values = context.Request.Headers.IfMatch;
        if (values.Count == 0) throw new ServerRequestException(428, "configuration_precondition_required");
        var value = values.ToString();
        if (values.Count != 1 || value.Length is < 3 or > 21 || value[0] != '"' || value[^1] != '"'
            || value[1] == '0' || !value[1..^1].All(char.IsAsciiDigit)
            || !long.TryParse(value[1..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var revision))
            throw new ServerRequestException(400, "invalid_configuration_precondition");
        return revision;
    }
    internal static string ETag(long revision) => '"' + revision.ToString(CultureInfo.InvariantCulture) + '"';
    internal static async Task<PublishFileConfiguration> ReadAsync(HttpContext context)
    {
        if (!context.Request.HasJsonContentType()) throw new ServerRequestException(415, "json_required");
        if (context.Request.ContentLength > MaxBytes) throw new ServerRequestException(413, "configuration_too_large");
        using var bytes = new MemoryStream();
        var buffer = new byte[8192]; int count;
        try
        {
            while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                if (bytes.Length + count > MaxBytes) throw new ServerRequestException(413, "configuration_too_large");
                bytes.Write(buffer, 0, count);
            }
        }
        catch (BadHttpRequestException error) when (error.StatusCode == 413)
        { throw new ServerRequestException(413, "configuration_too_large"); }
        try
        {
            var request = JsonSerializer.Deserialize<PublishFileConfiguration>(bytes.ToArray(), Json) ?? throw new JsonException();
            Validate(request.ProtocolVersion, request.Jobs);
            return request;
        }
        catch (JsonException) { throw new ServerRequestException(400, "invalid_file_configuration"); }
    }
    internal static void Validate(int protocol, IReadOnlyList<ManagedFileJob>? jobs)
    {
        if (protocol != 1 || jobs is not { Count: <= 256 } || jobs.Any(j => j is null || !Valid(j))
            || jobs.Select(j => j.Id).Distinct().Count() != jobs.Count)
            throw new ServerRequestException(400, "invalid_file_configuration");
    }
    private static bool Text(string? value, int limit) => !string.IsNullOrWhiteSpace(value) && value.Length <= limit && !value.Any(char.IsControl);
    private static bool Valid(ManagedFileJob j)
    {
        if (j.Id == Guid.Empty || !Text(j.Name, 256) || j.Kind is not ("FilesAndFolders" or "OneCFile")
            || j.StorageKind is not ("LocalDirectory" or "NetworkShare") || !WindowsPath(j.Source)
            || !WindowsPath(j.DestinationDirectory) || j.TemporaryDirectory is not null && !WindowsPath(j.TemporaryDirectory)
            || (j.StorageKind == "NetworkShare") != j.DestinationDirectory.Replace('/', '\\').StartsWith(@"\\")
            || j.KeepLast is < 1 or > 10000 || j.FullBackupEvery is < 1 or > 365
            || j.FileBackupMode is not ("Full" or "Incremental") || j.OneCMethod != "FileArchive"
            || j.FullBackupDayOfWeek is { } day && !Enum.IsDefined(day)
            || j.ScheduleKind is not ("Manual" or "Daily" or "Weekly" or "SystemStartup")
            || j.AdditionalDailyTimes is { Count: > 47 }
            || j.AdditionalDailyTimes is { } times && (times.Contains(j.DailyAt) || times.Distinct().Count() != times.Count)) return false;
        var days = (j.DaysOfWeek ?? "").Split(',').Select(d => d.Trim()).ToArray();
        string[] allowed = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];
        if (!(days.Length == 1 && days[0] == "None" && j.ScheduleKind != "Weekly")
            && (days.Length is < 1 or > 7 || days.Distinct().Count() != days.Length || days.Any(d => !allowed.Contains(d)))) return false;
        if (!Text(j.TimeZoneId, 128)) return false;
        try { if (j.TimeZoneId != "Local") _ = TimeZoneInfo.FindSystemTimeZoneById(j.TimeZoneId); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return false; }
        var source = Normal(j.Source);
        if (j.Kind == "OneCFile" && source.EndsWith(@"\1Cv8.1CD", StringComparison.OrdinalIgnoreCase)) source = source[..source.LastIndexOf('\\')];
        if (j.AdditionalDestinations is { Count: > 7 } || j.AdditionalDestinations?.Any(d => d is null
            || !WindowsPath(d.DestinationDirectory) || d.StorageKind is not ("LocalDirectory" or "NetworkShare")
            || (d.StorageKind == "NetworkShare") != d.DestinationDirectory.Replace('/', '\\').StartsWith(@"\\")
            || d.KeepLast is < 1 or > 10000) == true) return false;
        var destinations = new[] { j.DestinationDirectory }.Concat(j.AdditionalDestinations?.Select(d => d.DestinationDirectory) ?? []).ToArray();
        if (destinations.Any(path => Overlap(source, path) || j.TemporaryDirectory is not null && Overlap(j.TemporaryDirectory, path))
            || j.TemporaryDirectory is not null && Overlap(source, j.TemporaryDirectory)) return false;
        for (var index = 0; index < destinations.Length; index++)
            if (destinations.Skip(index + 1).Any(path => Overlap(destinations[index], path))) return false;
        return true;
    }
    private static string Normal(string path) => path.Replace('/', '\\').TrimEnd('\\');
    internal static bool Overlap(string left, string right)
    {
        left = Normal(left); right = Normal(right);
        return left.Equals(right, StringComparison.OrdinalIgnoreCase) || left.StartsWith(right + "\\", StringComparison.OrdinalIgnoreCase)
            || right.StartsWith(left + "\\", StringComparison.OrdinalIgnoreCase);
    }
    internal static bool WindowsPath(string? path)
    {
        if (!Text(path, 4096)) return false;
        var p = path!.Replace('/', '\\');
        var unc = p.StartsWith(@"\\");
        if (unc && path.Contains('/')) return false; // Core's NetworkShare contract requires native UNC separators.
        if (!unc && !(p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\')) return false;
        var rest = unc ? p[2..] : p[3..];
        var parts = rest.TrimEnd('\\').Split('\\');
        if (!unc && rest.Length == 0) return true;
        return (!unc || parts.Length >= 2) && parts.All(part => part.Length > 0 && part is not ("." or "..")
            && !part.EndsWith(' ') && !part.EndsWith('.') && !part.Any(c => "<>:\"|?*".Contains(c)) && !Device(part));
    }
    private static bool Device(string part)
    {
        var name = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || name.Length == 4 && (name[3] is >= '1' and <= '9' or '¹' or '²' or '³') && (name.StartsWith("COM") || name.StartsWith("LPT"));
    }
}
