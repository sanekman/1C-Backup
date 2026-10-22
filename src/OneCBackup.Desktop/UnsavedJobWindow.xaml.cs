using System.Windows;

namespace OneCBackup.Desktop;

internal enum UnsavedJobChoice { Return, Save, Discard }

public partial class UnsavedJobWindow : Window
{
    internal UnsavedJobChoice Choice { get; private set; }
    public UnsavedJobWindow() => InitializeComponent();
    private void Save_Click(object sender, RoutedEventArgs e)
    { Choice = UnsavedJobChoice.Save; DialogResult = true; }
    private void Discard_Click(object sender, RoutedEventArgs e)
    { Choice = UnsavedJobChoice.Discard; DialogResult = true; }
}
