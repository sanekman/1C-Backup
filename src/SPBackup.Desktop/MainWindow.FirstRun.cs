using System.Windows;
using System.Windows.Controls;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    internal Action<Window, TextBox>? FirstRunGuideOpened { get; set; }
    private void FirstRunGuide_Click(object sender, RoutedEventArgs e)
    {
        if (Jobs.SelectedItem is not BackupJob job || editorOpen) return;
        var text = new TextBox { Text = BackupSetupGuidance.FirstRun(job.Kind), IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0), Padding = new Thickness(18), FontSize = 14 };
        var panel = new DockPanel();
        var close = new Button { Content = "Понятно", IsCancel = true, IsDefault = true,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12), Padding = new Thickness(18, 7, 18, 7) };
        DockPanel.SetDock(close, Dock.Bottom); panel.Children.Add(close); panel.Children.Add(text);
        var guide = new Window { Owner = this, Title = "Первый запуск — " + job.Name, Content = panel,
            Width = 680, Height = 620, MinWidth = 440, MinHeight = 380, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        guide.SetResourceReference(BackgroundProperty, "CanvasBrush");
        close.Click += (_, _) => guide.Close();
        guide.Loaded += (_, _) => FirstRunGuideOpened?.Invoke(guide, text);
        guide.ShowDialog();
    }
}
