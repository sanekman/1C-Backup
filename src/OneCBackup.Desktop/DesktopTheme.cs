using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace OneCBackup.Desktop;

internal enum DesktopThemePreference { System, Light, Dark }

internal static class DesktopTheme
{
    private static readonly (string Key, string Light, string Dark)[] Palette =
    [
        ("LinkBrush", "#1D4ED8", "#9EC5FF"),
        ("CanvasBrush", "#F4F7FB", "#121820"),
        ("MutedBrush", "#64748B", "#A8B6C9"),
        ("LineBrush", "#E2E8F0", "#3A475A"),
        ("TextBrush", "#1E293B", "#E6EDF5"),
        ("SurfaceBrush", "White", "#1C2633"),
        ("ControlBrush", "#F1F5F9", "#273448"),
        ("BorderBrush", "#CBD5E1", "#506078"),
        ("AlternateBrush", "#FAFCFE", "#202D3C"),
        ("PanelBrush", "#F8FAFC", "#243142"),
        ("SelectionBrush", "#DBEAFE", "#244B78"),
        ("SelectionTextBrush", "#1E3A8A", "#EDF6FF"),
        ("WarningSurfaceBrush", "#FFFBEB", "#40341C"),
        ("WarningBrush", "#92400E", "#F8CD72"),
        ("ErrorBrush", "#B91C1C", "#FF9999"),
        ("SuccessBrush", "#166534", "#8BDC9E"),
        ("InfoSurfaceBrush", "#EFF6FF", "#213D58"),
        ("StatusSurfaceBrush", "#E8EEF5", "#263449"),
        ("StatusTextBrush", "#475569", "#CED9E6")
    ];
    public static DesktopThemePreference Preference { get; private set; } = DesktopThemePreference.System;
    private static bool isDark;
    private static bool windowHandlerRegistered;
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    private static void ApplyTitle(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var value = isDark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref value, sizeof(int));
    }
    private static string SettingsPath => Path.Combine(DesktopPaths.DataDirectory, "desktop-theme.txt");

    public static void Initialize()
    {
        try { if (Enum.TryParse<DesktopThemePreference>(File.ReadAllText(SettingsPath), out var value) && Enum.IsDefined(value)) Preference = value; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        if (!windowHandlerRegistered)
        {
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((_, args) => { if (args.OriginalSource is Window window) ApplyTitle(window); }));
            windowHandlerRegistered = true;
        }
        Apply();
        SystemEvents.UserPreferenceChanged -= SystemChanged;
        SystemEvents.UserPreferenceChanged += SystemChanged;
    }

    public static void Shutdown() => SystemEvents.UserPreferenceChanged -= SystemChanged;
    private static void SystemChanged(object sender, UserPreferenceChangedEventArgs args)
    {
        if (Preference == DesktopThemePreference.System)
            Application.Current.Dispatcher.BeginInvoke(new Action(Apply));
    }

    public static void Select(DesktopThemePreference value)
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
        Directory.CreateDirectory(DesktopPaths.DataDirectory);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, value.ToString());
        File.Move(temporary, SettingsPath, overwrite: true);
        Preference = value;
        Apply();
    }

    private static bool SystemIsDark()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); return key?.GetValue("AppsUseLightTheme") is int value && value == 0; }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    private static void Apply()
    {
        var dark = Preference == DesktopThemePreference.Dark || Preference == DesktopThemePreference.System && SystemIsDark();
        isDark = dark;
        var resources = Application.Current.Resources;
        foreach (var (key, lightColor, darkColor) in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(dark ? darkColor : lightColor);
            if (resources[key] is SolidColorBrush brush && !brush.IsFrozen) brush.Color = color;
            else resources[key] = new SolidColorBrush(color);
        }
        resources[SystemColors.WindowBrushKey] = resources["SurfaceBrush"];
        resources[SystemColors.WindowTextBrushKey] = resources["TextBrush"];
        resources[SystemColors.ControlBrushKey] = resources["ControlBrush"];
        resources[SystemColors.ControlTextBrushKey] = resources["TextBrush"];
        resources[SystemColors.HighlightBrushKey] = resources["SelectionBrush"];
        resources[SystemColors.HighlightTextBrushKey] = resources["SelectionTextBrush"];
        resources[SystemColors.InactiveSelectionHighlightBrushKey] = resources["SelectionBrush"];
        resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = resources["SelectionTextBrush"];
        foreach (Window window in Application.Current.Windows) ApplyTitle(window);
    }
}
