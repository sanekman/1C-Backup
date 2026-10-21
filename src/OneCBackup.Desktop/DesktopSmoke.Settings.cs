using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace OneCBackup.Desktop;

internal static partial class DesktopSmoke
{
    private static Task VerifySettingsSectionsAsync(MainWindow window, Action<string> screenshot)
    {
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Click(string name)
        {
            window.UpdateLayout(); var button = Control<Button>(name);
            if (!button.IsVisible || !button.IsEnabled) throw new InvalidOperationException("Раздел настроек недоступен: " + name);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
        }
        var width = window.Width; var height = window.Height;
        try
        {
            window.Width = 900; window.Height = 580;
            Click("AgentNavigation");
            var sections = new[] { "General", "Background", "Updates", "Connections", "Help" };
            if (!Control<StackPanel>("GeneralSettingsSection").IsVisible)
                throw new InvalidOperationException("Настройки начинаются с технического раздела.");
            foreach (var section in sections)
            {
                Click(section + "SettingsButton");
                foreach (var other in sections)
                    if (Control<StackPanel>(other + "SettingsSection").IsVisible != (section == other))
                        throw new InvalidOperationException("Настройки показывают сразу несколько разделов.");
                var action = section switch
                {
                    "General" => "ThemePicker", "Background" => "SaveStartupButton", "Updates" => "CheckUpdatesButton",
                    "Connections" => "ConnectServerMonitoringButton", _ => "ExportDiagnosticsButton"
                };
                var element = Control<FrameworkElement>(action); var scroll = Control<ScrollViewer>("SettingsContentScroll");
                var point = element.TranslatePoint(new Point(), scroll);
                if (!element.IsVisible || point.X < 0 || point.Y < 0 || point.X + element.ActualWidth > scroll.ActualWidth + 1
                    || point.Y + element.ActualHeight > scroll.ActualHeight + 1)
                    throw new InvalidOperationException("Основное действие настроек скрыто в компактном окне: " + action);
                foreach (var category in sections)
                {
                    var button = Control<Button>(category + "SettingsButton");
                    var position = button.TranslatePoint(new Point(), Control<Grid>("AgentView"));
                    if (!button.IsVisible || position.Y < 0 || position.Y + button.ActualHeight > scroll.TranslatePoint(new Point(), Control<Grid>("AgentView")).Y + 1)
                        throw new InvalidOperationException("Навигация настроек скрыта: " + category);
                }
                if (Control<ComboBox>("AgentModePicker").IsVisible || Control<PasswordBox>("GitHubUpdateToken").IsVisible
                    || Control<ComboBox>("ServiceAccount").IsVisible)
                    throw new InvalidOperationException("Технические настройки открыты по умолчанию.");
                if (section == "Help")
                {
                    string? opened = null;
                    window.UserGuideOpenOverride = path => opened = path;
                    Click("OpenUserGuideButton");
                    if (opened is null || !File.Exists(opened) || !File.Exists(Path.Combine(Path.GetDirectoryName(opened)!, "settings-overview.png")))
                        throw new InvalidOperationException("Пошаговое руководство или его изображение не включены в программу.");
                    var html = File.ReadAllText(opened);
                    if (!html.Contains("Скачать и установить") || !html.Contains("первую копию") || !html.Contains("восстанавливается"))
                        throw new InvalidOperationException("Руководство потеряло основные сценарии.");
                }
                screenshot("settings-" + section.ToLowerInvariant() + "-compact");
            }
            Click("GeneralSettingsButton");
        }
        finally { window.UserGuideOpenOverride = null; window.Width = width; window.Height = height; window.UpdateLayout(); }
        return Task.CompletedTask;
    }
}
