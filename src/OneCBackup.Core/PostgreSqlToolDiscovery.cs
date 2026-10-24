using System.Runtime.Versioning;
using Microsoft.Win32;

namespace OneCBackup.Core;

public sealed record PostgreSqlToolInstallation(string Directory, string Version, string PgDumpPath, string PgRestorePath, bool HasPsql)
{
    public string Label => "PostgreSQL " + Version + " — " + Directory;
}

public static class PostgreSqlToolDiscovery
{
    /// <summary>Inspect only known installation roots; never executes programs or searches user documents.</summary>
    public static IReadOnlyList<PostgreSqlToolInstallation> Find(IEnumerable<string> installationRoots)
    {
        var found = new Dictionary<string, PostgreSqlToolInstallation>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in installationRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) continue;
            try
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
                var bin = Path.GetFileName(full).Equals("bin", StringComparison.OrdinalIgnoreCase) ? full : Path.Combine(full, "bin");
                var dump = Path.Combine(bin, "pg_dump.exe"); var restore = Path.Combine(bin, "pg_restore.exe");
                if (!File.Exists(dump) || !File.Exists(restore)) continue;
                var name = Path.GetFileName(Path.GetDirectoryName(bin)!);
                found[bin] = new(bin, name, dump, restore, File.Exists(Path.Combine(bin, "psql.exe")));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { }
        }
        return found.Values.OrderByDescending(item => System.Version.TryParse(item.Version.Contains('.') ? item.Version : item.Version + ".0", out var version) ? version : new Version(0, 0))
            .ThenBy(item => item.Directory, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<PostgreSqlToolInstallation> FindInstalledOnWindows()
    {
        var roots = new List<string>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var installations = machine.OpenSubKey(@"SOFTWARE\PostgreSQL\Installations");
                if (installations is null) continue;
                foreach (var name in installations.GetSubKeyNames())
                {
                    using var installation = installations.OpenSubKey(name);
                    if (installation?.GetValue("Base Directory") is string path) roots.Add(path);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        foreach (var programFiles in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (string.IsNullOrWhiteSpace(programFiles)) continue;
            var postgres = Path.Combine(programFiles, "PostgreSQL");
            try { if (System.IO.Directory.Exists(postgres)) roots.AddRange(System.IO.Directory.EnumerateDirectories(postgres)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return Find(roots);
    }
}
