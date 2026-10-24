using System.Windows;
using System.Windows.Controls;
namespace OneCBackup.Desktop;
public partial class MainWindow
{
    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || ThemePicker.SelectedValue is not string text || !Enum.TryParse<DesktopThemePreference>(text, out var value)) return;
        try { DesktopTheme.Select(value); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        { ThemePicker.SelectedValue = DesktopTheme.Preference.ToString(); Status.Text = "Не удалось сохранить тему: " + error.Message; }
    }
}
