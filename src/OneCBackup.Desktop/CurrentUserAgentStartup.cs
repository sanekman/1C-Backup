using System.IO;
using Microsoft.Win32;

namespace OneCBackup.Desktop;

internal static class CurrentUserAgentStartup
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OneCBackupAgent";
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
        key.SetValue(ValueName, $"\"{executable}\" --run --quiet", RegistryValueKind.String);
    }
}
