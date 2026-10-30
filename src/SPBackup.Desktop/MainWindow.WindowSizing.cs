using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private void FitInitialWindowToWorkArea()
    {
        // Screen bounds are physical pixels; Window.Height is in WPF logical units.
        var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
        var available = Math.Max(240, screen.WorkingArea.Height / VisualTreeHelper.GetDpi(this).DpiScaleY - 24);
        MinHeight = Math.Min(MinHeight, available);
        Height = Math.Min(Height, available);
    }
}
