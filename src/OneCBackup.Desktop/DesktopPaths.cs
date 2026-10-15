using System.IO;

namespace OneCBackup.Desktop;

internal static class DesktopPaths
{
    public static string DataDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OneCBackup");
}
