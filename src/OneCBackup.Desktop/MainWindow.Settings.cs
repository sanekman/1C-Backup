using System.Windows;
using System.Windows.Controls;

namespace OneCBackup.Desktop;

public partial class MainWindow
{
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
