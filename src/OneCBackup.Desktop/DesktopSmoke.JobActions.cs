using System.Windows;
using System.Windows.Controls;

namespace OneCBackup.Desktop;

internal static partial class DesktopSmoke
{
    private static async Task CheckJobActionLayoutAsync(MainWindow window)
    {
        var originalWidth = window.Width;
        var originalHeight = window.Height;
        var panel = (WrapPanel)window.FindName("JobActions");
        var summary = (TextBlock)window.FindName("SelectedJobSummary");
        try
        {
            foreach (var width in new[] { 900d, 1100d, 1400d })
            {
                window.Width = width;
                window.Height = 580;
                await window.Dispatcher.InvokeAsync(window.UpdateLayout,
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var buttons = panel.Children.OfType<Button>().Where(button => button.IsVisible).ToArray();
                var bounds = buttons.Select(button => button.TransformToAncestor(panel)
                    .TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight))).ToArray();
                for (var i = 0; i < bounds.Length; i++)
                {
                    var current = bounds[i];
                    if (current.Width <= 0 || current.Height <= 0 || current.Left < -1
                        || current.Right > panel.ActualWidth + 1 || current.Bottom > panel.ActualHeight + 1)
                        throw new InvalidOperationException("Кнопка выходит за границы панели: " + buttons[i].Name);
                    for (var j = i + 1; j < bounds.Length; j++)
                    {
                        var other = bounds[j];
                        var horizontalGap = Math.Max(current.Left, other.Left) - Math.Min(current.Right, other.Right);
                        var verticalGap = Math.Max(current.Top, other.Top) - Math.Min(current.Bottom, other.Bottom);
                        if (horizontalGap < 5 && verticalGap < 5)
                            throw new InvalidOperationException("Кнопки перекрываются или не имеют отступа: "
                                + buttons[i].Name + ", " + buttons[j].Name);
                    }
                }
                var panelBottom = panel.TranslatePoint(new Point(0, panel.ActualHeight), window).Y;
                if (summary.TranslatePoint(new Point(), window).Y < panelBottom)
                    throw new InvalidOperationException("Подсказка перекрывает панель действий.");
            }
        }
        finally
        {
            window.Width = originalWidth;
            window.Height = originalHeight;
            window.UpdateLayout();
        }
    }
}
