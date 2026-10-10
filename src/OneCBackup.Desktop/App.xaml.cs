using System.Windows;
using System.IO;
namespace OneCBackup.Desktop;
public partial class App : Application
{
    private FileStream? instanceLock;

    protected override void OnStartup(StartupEventArgs e)
    {
        string? smokeOutput = null;
        if (e.Args.Length == 2 && e.Args[0] == "--smoke-ui")
        {
            smokeOutput = Path.GetFullPath(e.Args[1]);
            Directory.CreateDirectory(smokeOutput);
            DesktopPaths.DataDirectory = Path.Combine(smokeOutput, "profile-" + Guid.NewGuid().ToString("N"));
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            DispatcherUnhandledException += (_, error) =>
            { error.Handled = true; DesktopSmoke.Finish(smokeOutput, false, error.Exception.ToString()); };
        }
        try
        {
            var directory = DesktopPaths.DataDirectory;
            Directory.CreateDirectory(directory);
            instanceLock = new FileStream(Path.Combine(directory, "desktop.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (smokeOutput is not null) { DesktopSmoke.Finish(smokeOutput, false, ex.ToString()); return; }
            MessageBox.Show("Не удалось открыть рабочий каталог. Возможно, программа уже запущена. " + ex.Message, "1C-Backup");
            Shutdown(1);
            return;
        }
        base.OnStartup(e);
        if (smokeOutput is not null)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle,
                new Action(async () => await DesktopSmoke.RunAsync(smokeOutput)));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instanceLock?.Dispose();
        base.OnExit(e);
    }
}
