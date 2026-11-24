using System.Reflection;
using System.Runtime.InteropServices;

namespace SPBackup.Desktop;

internal static class ApplicationBuildInfo
{
    private static readonly string InformationalVersion = typeof(ApplicationBuildInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "неизвестна";
    public static string Version => InformationalVersion.Split('+', 2)[0];
    public static string Revision => InformationalVersion.Contains('+') ? InformationalVersion.Split('+', 2)[1] : "не указана";
    public static string Details => $"Сборка: {Revision}\n.NET {Environment.Version} · {RuntimeInformation.ProcessArchitecture}\n{RuntimeInformation.OSDescription}";
    // Deliberately excludes profile paths, user names, jobs and credentials.
    public static string SupportText => $"SP-Backup\nВерсия: {Version}\n{Details}";
}
