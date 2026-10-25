using System.IO;
using Microsoft.Win32;

namespace SPBackup.Desktop;

internal static class CurrentUserAgentStartup
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OneCBackupAgent";
    private static string TrayCommand => $"\"{Path.Combine(AppContext.BaseDirectory, "SPBackup.Desktop.exe")}\" --tray --start-agent";

    public static void UpgradeOwnedLegacyEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        var current = key?.GetValue(ValueName) as string;
        var owned = new[] {
            $"\"{Path.Combine(AppContext.BaseDirectory, "OneCBackup.Agent.exe")}\" --run --quiet",
            $"\"{Path.Combine(AppContext.BaseDirectory, "SPBackup.Agent.exe")}\" --run --quiet",
            $"\"{Path.Combine(AppContext.BaseDirectory, "OneCBackup.Desktop.exe")}\" --tray --start-agent" };
        if (owned.Any(command => string.Equals(current, command, StringComparison.OrdinalIgnoreCase)))
            key!.SetValue(ValueName, TrayCommand, RegistryValueKind.String);
    }
    public static void RemoveOwnedEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (string.Equals(key?.GetValue(ValueName) as string, TrayCommand, StringComparison.OrdinalIgnoreCase))
            key!.DeleteValue(ValueName, false);
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
        var executable = Path.Combine(AppContext.BaseDirectory, "SPBackup.Agent.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Фоновый агент не найден рядом с приложением.", executable);
        key.SetValue(ValueName, TrayCommand, RegistryValueKind.String);
    }
}
