using System.Windows;
using System.IO;
using OneCBackup.Core;
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
        DiagnosticLog.Start(DesktopPaths.DataDirectory, DiagnosticComponent.Desktop, typeof(App).Assembly);
        DispatcherUnhandledException += (_, error) => DiagnosticLog.Current.Write(DiagnosticEvent.UnhandledException, DiagnosticLevel.Error, error.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, error) => DiagnosticLog.Current.Write(DiagnosticEvent.UnhandledException, DiagnosticLevel.Error, error.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, error) => DiagnosticLog.Current.Write(DiagnosticEvent.UnobservedTaskException, DiagnosticLevel.Error, error.Exception);
        try
        {
            var directory = DesktopPaths.DataDirectory;
            Directory.CreateDirectory(directory);
            instanceLock = new FileStream(Path.Combine(directory, "desktop.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Current.Write(DiagnosticEvent.ProcessError, DiagnosticLevel.Error, ex);
            if (smokeOutput is not null) { DesktopSmoke.Finish(smokeOutput, false, ex.ToString()); return; }
            MessageBox.Show("Не удалось открыть рабочий каталог. Возможно, программа уже запущена. " + ex.Message, "1C-Backup");
            Shutdown(1);
            return;
        }
        DesktopTheme.Initialize();
        base.OnStartup(e);
        if (smokeOutput is not null)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle,
                new Action(async () => await DesktopSmoke.RunAsync(smokeOutput)));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DiagnosticLog.Current.Write(DiagnosticEvent.ProcessStopped, values: new(Code: e.ApplicationExitCode));
        DesktopTheme.Shutdown();
        instanceLock?.Dispose();
        base.OnExit(e);
    }
}
