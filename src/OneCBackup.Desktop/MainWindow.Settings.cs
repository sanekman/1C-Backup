using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace OneCBackup.Desktop;

public partial class MainWindow
{
    internal Action<string>? UserGuideOpenOverride { get; set; }
    private void OpenUserGuide_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Help", "user-guide.html");
        try
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Файл инструкции не найден.", path);
            if (UserGuideOpenOverride is { } open) open(path);
            else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception error) { Status.Text = "Не удалось открыть инструкцию: " + error.Message + " Файл: " + path; }
    }

    private void SettingsSection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string selected }) return;
        foreach (var (button, panel, key) in new[]
        {
            (GeneralSettingsButton, GeneralSettingsSection, "general"),
            (BackgroundSettingsButton, BackgroundSettingsSection, "background"),
            (UpdatesSettingsButton, UpdatesSettingsSection, "updates"),
            (ConnectionsSettingsButton, ConnectionsSettingsSection, "connections"),
            (HelpSettingsButton, HelpSettingsSection, "help")
        })
        {
            var active = key == selected;
            panel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            button.Tag = active ? "Active" : null;
            button.Style = active ? (Style)FindResource("PrimaryButton") : (Style)FindResource(typeof(Button));
        }
        SettingsContentScroll.ScrollToTop();
    }
}
