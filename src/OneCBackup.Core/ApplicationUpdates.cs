using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OneCBackup.Core;

public sealed record ApplicationUpdateSettings
{
    public bool AutomaticChecks { get; init; } = true;
    public bool UpdateOnExit { get; init; }
    public bool IncludePreview { get; init; } = true;
    public bool HasGitHubToken { get; init; }
}

public sealed class ApplicationUpdateSettingsStore(string path)
{
    private readonly AgentJsonFile<ApplicationUpdateSettings> file = new(path);
    public async Task<ApplicationUpdateSettings> LoadAsync(CancellationToken token = default) => await file.ReadAsync(token) ?? new();
    public Task SaveAsync(ApplicationUpdateSettings settings, CancellationToken token = default) => file.WriteAsync(settings, token);
}

public sealed record UpdateManifest(int SchemaVersion, string Version, string Platform, string InstallerKind,
    string InstallerFile, long Size, string Sha256)
{
    public void Validate(string releaseVersion)
    {
        if (SchemaVersion != 1 || Version != releaseVersion || Platform != "win-x64" || InstallerKind is not ("nsis" or "inno")
            || InstallerFile != "1C-Backup-windows-x64-setup.exe" || Size is < 2 or > 100 * 1024 * 1024
            || Sha256 is null || !Regex.IsMatch(Sha256, "^[a-fA-F0-9]{64}$"))
            throw new InvalidDataException("Некорректные сведения об обновлении. Установка запрещена.");
    }
}

public sealed record ApplicationUpdate(UpdateManifest Manifest, string DistributionBranch)
{
    public string ReleaseAddress => "https://github.com/sanekman/1C-Backup/releases/tag/v" + Manifest.Version;
}

/// <summary>Strict SemVer ordering: numeric preview components, no downgrade, stable after prerelease.</summary>
public sealed class ApplicationReleaseVersion : IComparable<ApplicationReleaseVersion>
{
    private readonly Version number;
    private readonly string[] labels;
    public bool IsPreview => labels.Length > 0;
    private ApplicationReleaseVersion(Version number, string[] labels) { this.number = number; this.labels = labels; }
    public static ApplicationReleaseVersion Parse(string text)
    {
        var match = Regex.Match(text, @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z]+(?:\.[0-9A-Za-z]+)*))?(?:\+[0-9A-Za-z.]+)?$");
        if (!match.Success) throw new FormatException("Некорректный номер версии программы.");
        return new(Version.Parse(string.Join('.', match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value)),
            match.Groups[4].Success ? match.Groups[4].Value.Split('.') : []);
    }
    public int CompareTo(ApplicationReleaseVersion? other)
    {
        if (other is null) return 1;
        var result = number.CompareTo(other.number); if (result != 0) return result;
        if (labels.Length == 0 || other.labels.Length == 0) return other.labels.Length.CompareTo(labels.Length);
        for (var i = 0; i < Math.Min(labels.Length, other.labels.Length); i++)
        {
            var leftNumeric = labels[i].All(char.IsAsciiDigit); var rightNumeric = other.labels[i].All(char.IsAsciiDigit);
            result = leftNumeric && rightNumeric
                ? CompareNumeric(labels[i], other.labels[i])
                : leftNumeric != rightNumeric ? leftNumeric ? -1 : 1 : string.CompareOrdinal(labels[i], other.labels[i]);
            if (result != 0) return result;
        }
        return labels.Length.CompareTo(other.labels.Length);
    }
    private static int CompareNumeric(string left, string right)
    {
        left = left.TrimStart('0'); right = right.TrimStart('0');
        var length = left.Length.CompareTo(right.Length);
        return length != 0 ? length : string.CompareOrdinal(left, right);
    }
}

/// <summary>Only fixed GitHub API routes are used. Secrets are never placed in URLs or error messages.</summary>
public sealed class GitHubApplicationUpdates(HttpClient client)
{
    private const string Api = "https://api.github.com/repos/sanekman/1C-Backup/";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };

    private static HttpRequestMessage Request(string path, string? token, bool raw = false)
    {
        if (token is not null && (token.Length is < 1 or > 1024 || token.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-')))
            throw new ArgumentException("Некорректный токен GitHub.");
        var request = new HttpRequestMessage(HttpMethod.Get, Api + path)
        { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        request.Headers.UserAgent.ParseAdd("OneCBackup-Updater/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(raw ? "application/vnd.github.raw" : "application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            throw new InvalidOperationException("GitHub не предоставил доступ. Для закрытого репозитория сохраните токен с правом Contents: Read; проверьте его срок и лимит запросов.");
        if (!response.IsSuccessStatusCode) throw new IOException($"Не удалось получить обновление с GitHub (HTTP {(int)response.StatusCode}).");
    }
    private async Task<byte[]> ReadSmallAsync(string path, string? secret, bool raw, CancellationToken token)
    {
        using var request = Request(path, secret, raw);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        EnsureSuccess(response);
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + count > 1024 * 1024) throw new InvalidDataException("Сведения об обновлении слишком велики.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    public async Task<ApplicationUpdate?> CheckAsync(string currentVersion, bool includePreview, string? secret = null, CancellationToken token = default)
    {
        var current = ApplicationReleaseVersion.Parse(currentVersion);
        using var releases = JsonDocument.Parse(await ReadSmallAsync("releases?per_page=50", secret, false, token));
        var candidates = new List<(string Version, string Branch, ApplicationReleaseVersion Order)>();
        foreach (var release in releases.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean() || !includePreview && release.GetProperty("prerelease").GetBoolean()) continue;
            var tag = release.GetProperty("tag_name").GetString() ?? "";
            if (!tag.StartsWith('v')) continue;
            ApplicationReleaseVersion version;
            try { version = ApplicationReleaseVersion.Parse(tag[1..]); } catch (FormatException) { continue; }
            if (version.CompareTo(current) <= 0 || !includePreview && version.IsPreview) continue;
            var pointer = Regex.Match(release.GetProperty("body").GetString() ?? "",
                @"<!-- onec-update-manifest: (distribution/windows-x64-[A-Za-z0-9.\-]+) -->");
            if (!pointer.Success) continue;
            candidates.Add((tag[1..], pointer.Groups[1].Value, version));
        }
        var latest = candidates.OrderByDescending(candidate => candidate.Order).FirstOrDefault();
        if (latest.Order is null) return null;
        var bytes = await ReadSmallAsync("contents/windows/update-manifest.json?ref=" + Uri.EscapeDataString(latest.Branch), secret, true, token);
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(bytes, Json) ?? throw new InvalidDataException("Нет сведений об обновлении.");
        manifest.Validate(latest.Version);
        return new(manifest, latest.Branch);
    }

    public async Task<string> DownloadAsync(ApplicationUpdate update, string directory, string? secret = null,
        IProgress<int>? progress = null, CancellationToken token = default)
    {
        update.Manifest.Validate(update.Manifest.Version);
        if (!Regex.IsMatch(update.DistributionBranch, @"^distribution/windows-x64-[A-Za-z0-9.\-]+$"))
            throw new InvalidDataException("Некорректный источник обновления.");
        var root = Path.GetFullPath(directory); Directory.CreateDirectory(root);
        var target = Path.Combine(root, update.Manifest.Sha256.ToLowerInvariant() + ".exe");
        if (File.Exists(target) && await IsVerifiedAsync(target, update.Manifest, token)) { progress?.Report(100); return target; }
        if (File.Exists(target)) File.Delete(target);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var request = Request("contents/windows/" + update.Manifest.InstallerFile + "?ref=" + Uri.EscapeDataString(update.DistributionBranch), secret, true);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            EnsureSuccess(response);
            if (response.Content.Headers.ContentLength is long size && size != update.Manifest.Size)
                throw new InvalidDataException("Размер скачиваемого обновления не совпадает с опубликованным.");
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920]; long copied = 0; int count;
                while ((count = await input.ReadAsync(buffer, token)) != 0)
                {
                    copied += count;
                    if (copied > update.Manifest.Size) throw new InvalidDataException("Размер обновления превышает опубликованный.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    progress?.Report((int)(copied * 100 / update.Manifest.Size));
                }
                await output.FlushAsync(token); output.Flush(true);
            }
            if (!await IsVerifiedAsync(temporary, update.Manifest, token))
                throw new InvalidDataException("Контрольная сумма обновления не совпадает. Установка запрещена.");
            token.ThrowIfCancellationRequested(); File.Move(temporary, target); progress?.Report(100); return target;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static async Task<bool> IsVerifiedAsync(string path, UpdateManifest manifest, CancellationToken token = default)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        if (input.Length != manifest.Size || input.ReadByte() != 'M' || input.ReadByte() != 'Z') return false;
        input.Position = 0;
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token)).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase);
    }
}
