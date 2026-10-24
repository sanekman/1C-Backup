using System.Runtime.InteropServices;
using System.Windows;

namespace OneCBackup.Desktop;

public partial class MainWindow
{
    private void CopyBuildInfo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ApplicationBuildInfo.SupportText);
            BuildInfoStatus.Text = "Сведения скопированы. Их можно вставить в сообщение об ошибке.";
        }
        catch (ExternalException)
        {
            BuildInfoStatus.Text = "Буфер обмена занят. Повторите копирование или выделите сведения вручную.";
        }
    }
}
