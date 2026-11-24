using System.IO;
using Microsoft.Win32;
using SPBackup.Core;

namespace SPBackup.Desktop;

internal static class CurrentUserAgentStartup
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OneCBackupAgent";
    private static AgentAutostartCommands Commands => new(AppContext.BaseDirectory);

    public static void UpgradeOwnedLegacyEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (Commands.Owns(key?.GetValue(ValueName) as string))
            key!.SetValue(ValueName, Commands.Current, RegistryValueKind.String);
    }
    public static void RemoveOwnedEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (Commands.Owns(key?.GetValue(ValueName) as string)) key!.DeleteValue(ValueName, false);
    }
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return Commands.Owns(key?.GetValue(ValueName) as string);
    }
    public static void Save(bool enabled)
    {
        if (!enabled) { RemoveOwnedEntry(); return; }
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        var existing = key.GetValue(ValueName);
        if (existing is not null && (existing is not string command || !Commands.CanEnable(command)))
            throw new InvalidOperationException("Автозапуск настроен для другой копии программы. Откройте её и выключите автозапуск, затем включите его здесь.");
        var executable = Path.Combine(AppContext.BaseDirectory, "SPBackup.Agent.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Фоновый агент не найден рядом с приложением.", executable);
        key.SetValue(ValueName, Commands.Current, RegistryValueKind.String);
    }
}
