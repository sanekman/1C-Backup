namespace SPBackup.Core;

public sealed record OneCInstalledPlatform(Version Version, string ExecutablePath);

public static class OneCPlatformLocator
{
    public static bool IsVersionSelectorValid(string? selector) => TrySelector(selector, out _);
    private static bool TrySelector(string? selector, out int[] parts)
    {
        parts = [];
        var value = (selector ?? "").Trim();
        if (value.Length == 0) return true;
        if (value.EndsWith(".*", StringComparison.Ordinal)) value = value[..^2];
        var tokens = value.Split('.');
        if (tokens.Length is < 2 or > 4) return false;
        var numbers = new List<int>();
        foreach (var token in tokens)
        {
            if (token.Length == 0 || !token.All(char.IsAsciiDigit) || !int.TryParse(token, out var number)) return false;
            numbers.Add(number);
        }
        parts = numbers.ToArray(); return true;
    }
    public static IReadOnlyList<OneCInstalledPlatform> Discover(IEnumerable<string>? roots = null)
    {
        roots ??= new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.Combine(p, "1cv8"));
        var result = new List<OneCInstalledPlatform>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var directory in Directory.EnumerateDirectories(root))
                {
                    if (!Version.TryParse(Path.GetFileName(directory), out var version)) continue;
                    var executable = Path.Combine(directory, "bin", "1cv8.exe");
                    if (File.Exists(executable)) result.Add(new(version, Path.GetFullPath(executable)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result.OrderByDescending(p => p.Version).ToArray();
    }
    public static string Resolve(OneCSettings settings, IEnumerable<OneCInstalledPlatform>? installed = null)
    {
        var errors = settings.Validate();
        if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
        if (!settings.AutoSelectPlatform)
        {
            if (!File.Exists(settings.ExecutablePath)) throw new FileNotFoundException("Не найдена платформа 1С.", settings.ExecutablePath);
            return settings.ExecutablePath;
        }
        TrySelector(settings.PlatformVersion, out var parts);
        var selected = (installed ?? Discover()).OrderByDescending(p => p.Version).FirstOrDefault(p =>
        {
            int[] version = [p.Version.Major, p.Version.Minor, p.Version.Build, p.Version.Revision];
            return parts.Select((part, index) => part == version[index]).All(matches => matches) && File.Exists(p.ExecutablePath);
        });
        return selected?.ExecutablePath ?? throw new FileNotFoundException(string.IsNullOrWhiteSpace(settings.PlatformVersion)
            ? "Не найдена установленная платформа 1С. Установите её или выберите 1cv8.exe вручную."
            : $"Не найдена установленная платформа 1С версии {settings.PlatformVersion}. Установите указанную версию или выберите программу вручную.");
    }
}
