using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace SPBackup.Desktop;

internal static partial class DesktopSmoke
{
    private static void VerifyGuideLink(MainWindow window, string buttonName, int section, string heading)
    {
        var button = (Button)window.FindName(buttonName);
        var source = ((TextBox)window.FindName("Source")).Text;
        var progress = ((TextBlock)window.FindName("WizardProgress")).Text;
        var previousOpen = window.UserGuideOpenOverride;
        string? opened = null;
        try
        {
            window.UserGuideOpenOverride = target => opened = target;
            window.UpdateLayout();
            var point = button.TranslatePoint(new Point(), window);
            if (!button.IsVisible || !button.IsEnabled || point.X < 0 || point.Y < 0
                || point.X + button.ActualWidth > window.ActualWidth || point.Y + button.ActualHeight > window.ActualHeight)
                throw new InvalidOperationException("Помощь недоступна в текущем окне: " + buttonName);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!Uri.TryCreate(opened, UriKind.Absolute, out var uri) || !uri.IsFile || uri.Fragment != "#section-" + section)
                throw new InvalidOperationException("Помощь не открывает нужный раздел локальной инструкции.");
            var html = File.ReadAllText(uri.LocalPath);
            if (!html.Contains("<h2 id=\"section-" + section + "\">" + section + ". " + heading + "</h2>"))
                throw new InvalidOperationException("Ссылка помощи потеряла раздел: " + heading);
            if (((TextBox)window.FindName("Source")).Text != source || ((TextBlock)window.FindName("WizardProgress")).Text != progress)
                throw new InvalidOperationException("Открытие инструкции изменило введённые данные или шаг мастера.");
        }
        finally { window.UserGuideOpenOverride = previousOpen; }
    }

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
                if (Control<ComboBox>("AgentModePicker").IsVisible != (section == "Connections") || Control<PasswordBox>("GitHubUpdateToken").IsVisible
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
