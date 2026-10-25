using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    internal Action<string>? UserGuideOpenOverride { get; set; }
    private void OpenUserGuide_Click(object sender, RoutedEventArgs e) => OpenUserGuide();

    private void OpenFirstCopyGuide_Click(object sender, RoutedEventArgs e) => OpenUserGuide(2);

    private void OpenWizardHelp_Click(object sender, RoutedEventArgs e) => OpenUserGuide(wizardStep switch
    {
        0 => Kind.SelectedItem switch
        {
            DatabaseKind.OneCFile => 7,
            DatabaseKind.SqlServer or DatabaseKind.PostgreSql => 9,
            DatabaseKind.WebDav => 15,
            _ => 2
        },
        1 => 8,
        2 => 4,
        _ => 3
    });

    private void OpenUserGuide(int? section = null)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Help", "user-guide.html");
        try
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Файл инструкции не найден.", path);
            var target = section is null ? path : new Uri(path).AbsoluteUri + "#section-" + section;
            if (UserGuideOpenOverride is { } open) open(target);
            else Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
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
