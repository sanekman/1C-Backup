using System.IO;
using Microsoft.Win32;

namespace OneCBackup.Desktop;

internal static class CurrentUserAgentStartup
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OneCBackupAgent";
    private static string TrayCommand => $"\"{Path.Combine(AppContext.BaseDirectory, "OneCBackup.Desktop.exe")}\" --tray --start-agent";

    public static void UpgradeOwnedLegacyEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        var legacy = $"\"{Path.Combine(AppContext.BaseDirectory, "OneCBackup.Agent.exe")}\" --run --quiet";
        if (string.Equals(key?.GetValue(ValueName) as string, legacy, StringComparison.OrdinalIgnoreCase))
            key!.SetValue(ValueName, TrayCommand, RegistryValueKind.String);
    }
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) is string;
    }
    public static void Save(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        if (!enabled) { key.DeleteValue(ValueName, throwOnMissingValue: false); return; }
        var executable = Path.Combine(AppContext.BaseDirectory, "OneCBackup.Agent.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Фоновый агент не найден рядом с приложением.", executable);
        key.SetValue(ValueName, TrayCommand, RegistryValueKind.String);
    }
}
