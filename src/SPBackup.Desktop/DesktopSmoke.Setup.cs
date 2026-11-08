using System.Windows;
using System.Windows.Controls;
using SPBackup.Core;

namespace SPBackup.Desktop;

internal static partial class DesktopSmoke
{
    private static void VerifySetupSourceExamples(MainWindow window)
    {
        var kind = (ComboBox)window.FindName("Kind");
        var source = (TextBox)window.FindName("Source");
        var name = (TextBox)window.FindName("JobName");
        var originalName = name.Text; var originalSource = source.Text;
        try
        {
            foreach (var value in Enum.GetValues<DatabaseKind>())
            {
                kind.SelectedItem = value;
                if (((TextBlock)window.FindName("WizardHint")).Text != BackupSetupGuidance.SourceExample(value))
                    throw new InvalidOperationException("Пример источника не соответствует типу задания.");
            }
            foreach (var value in new[] { DatabaseKind.SqlServer, DatabaseKind.PostgreSql })
            {
                kind.SelectedItem = value; name.Text = "Проверка ввода"; source.Text = @"C:\backup\wrong.bak";
                ((Button)window.FindName("NextStepButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!((TextBlock)window.FindName("WizardError")).Text.Contains("только точное имя базы")
                    || !((StackPanel)window.FindName("BaseStep")).IsVisible)
                    throw new InvalidOperationException("Путь к архиву принят за имя SQL-базы.");
            }
        }
        finally { kind.SelectedItem = DatabaseKind.OneCFile; source.Text = originalSource; name.Text = originalName; }
    }

    private static void VerifyFirstRunGuide(MainWindow window, Action<string, Window> screenshot)
    {
        var originalWidth = window.Width; var originalHeight = window.Height;
        try
        {
            foreach (var compact in new[] { false, true })
            {
                window.Width = compact ? 900 : originalWidth; window.Height = compact ? 580 : originalHeight;
                window.UpdateLayout(); var opened = false;
                window.FirstRunGuideOpened = (guide, text) => guide.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        opened = text.IsReadOnly && text.Text.Contains("5.") && text.Text.Contains("Проводника")
                            && text.Text.Contains("тестовую базу") && guide.ActualHeight <= window.ActualHeight + 1
                            && guide.ActualWidth <= window.ActualWidth + 1;
                        screenshot(compact ? "first-run-guide-compact" : "first-run-guide", guide);
                    }
                    finally { guide.Close(); }
                }));
                ((MenuItem)window.FindName("FirstRunGuideMenuItem")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                if (!opened) throw new InvalidOperationException("Инструкция первого запуска не объясняет проверку/восстановление или не помещается в окно владельца.");
            }
        }
        finally
        {
            window.FirstRunGuideOpened = null; window.Width = originalWidth; window.Height = originalHeight; window.UpdateLayout();
        }
    }
}
