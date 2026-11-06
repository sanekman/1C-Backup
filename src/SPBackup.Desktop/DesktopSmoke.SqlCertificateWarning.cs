using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace SPBackup.Desktop;

internal static partial class DesktopSmoke
{
    private static async Task VerifySqlCertificateWarningAsync(Window owner)
    {
        var checkbox = (CheckBox)owner.FindName("SqlTrustServerCertificate");
        var ownerHandle = new WindowInteropHelper(owner).Handle;
        var thread = CertificateGetWindowThreadProcessId(ownerHandle, out _);
        foreach (var accept in new[] { false, true })
        {
            var response = Task.Run(async () =>
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline)
                {
                    nint dialog = 0;
                    CertificateEnumThreadWindows(thread, (handle, _) =>
                    {
                        var title = new StringBuilder(256);
                        CertificateGetWindowText(handle, title, title.Capacity);
                        if (CertificateGetWindow(handle, 4) == ownerHandle
                            && title.ToString() == "MS SQL: отключение проверки сертификата") dialog = handle;
                        return true;
                    }, 0);
                    if (dialog != 0)
                    {
                        var no = CertificateGetDlgItem(dialog, 7);
                        var yes = CertificateGetDlgItem(dialog, 6);
                        var defaultIsNo = no != 0 && (CertificateGetWindowLong(no, -16) & 15) == 1;
                        // Always close our own dialog, including when an assertion will fail.
                        if (!CertificatePostMessage(accept && defaultIsNo ? yes : no, 0x00F5, 0, 0))
                            throw new InvalidOperationException("Не удалось ответить на предупреждение сертификата.");
                        if (yes == 0 || !defaultIsNo)
                            throw new InvalidOperationException("В предупреждении сертификата нет безопасного выбора «Нет» по умолчанию.");
                        return;
                    }
                    await Task.Delay(20);
                }
                throw new InvalidOperationException("Предупреждение сертификата MS SQL не открылось.");
            });
            checkbox.IsChecked = true;
            checkbox.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            await response;
            if (checkbox.IsChecked != accept)
                throw new InvalidOperationException("Выбор в предупреждении сертификата не применился к заданию.");
        }
    }

    private delegate bool CertificateWindowCallback(nint handle, nint parameter);
    [DllImport("user32.dll", EntryPoint = "EnumThreadWindows")]
    private static extern bool CertificateEnumThreadWindows(uint thread, CertificateWindowCallback callback, nint parameter);
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint CertificateGetWindowThreadProcessId(nint handle, out uint process);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int CertificateGetWindowText(nint handle, StringBuilder text, int count);
    [DllImport("user32.dll", EntryPoint = "GetWindow")]
    private static extern nint CertificateGetWindow(nint handle, uint command);
    [DllImport("user32.dll", EntryPoint = "GetDlgItem")]
    private static extern nint CertificateGetDlgItem(nint handle, int id);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int CertificateGetWindowLong(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool CertificatePostMessage(nint handle, uint message, nint wparam, nint lparam);
}
