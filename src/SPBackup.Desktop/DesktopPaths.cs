using System.IO;

namespace SPBackup.Desktop;

internal static class DesktopPaths
{
    public static string DataDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OneCBackup");
}
