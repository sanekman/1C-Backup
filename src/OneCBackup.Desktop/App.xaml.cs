using System.Windows;
using System.IO;
using OneCBackup.Core;
namespace OneCBackup.Desktop;
public partial class App : Application
{
    private FileStream? instanceLock;
    private EventWaitHandle? showEvent;
    private RegisteredWaitHandle? showRegistration;
    private bool pendingShow;
    internal bool StartInTray { get; private set; }
    internal bool StartAgent { get; private set; }
    internal bool StartWindowSchedule { get; private set; }

    private static string ShowEventName => @"Local\OneCBackup.Show." +
        System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value + "." +
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(DesktopPaths.DataDirectory).ToUpperInvariant())));

    private void ShowExistingWindow()
    {
        if (MainWindow is MainWindow window && window.IsLoaded) window.ShowFromTray();
        else pendingShow = true;
    }

    internal void WindowReady()
    {
        if (pendingShow) { pendingShow = false; ShowExistingWindow(); }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        StartInTray = e.Args.Contains("--tray");
        StartAgent = e.Args.Contains("--start-agent");
        StartWindowSchedule = e.Args.Contains("--start-window-scheduler");
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
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            showRegistration = ThreadPool.RegisterWaitForSingleObject(showEvent,
                (_, _) => Dispatcher.BeginInvoke(new Action(ShowExistingWindow)), null, Timeout.Infinite, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Current.Write(DiagnosticEvent.ProcessError, DiagnosticLevel.Error, ex);
            if (smokeOutput is not null) { DesktopSmoke.Finish(smokeOutput, false, ex.ToString()); return; }
            if (ex is IOException)
            {
                try
                {
                    using var existing = EventWaitHandle.OpenExisting(ShowEventName);
                    if (!StartInTray) existing.Set();
                    Shutdown();
                    return;
                }
                catch (WaitHandleCannotBeOpenedException) { }
                catch (UnauthorizedAccessException) { }
            }
            MessageBox.Show("Не удалось открыть рабочий каталог. Возможно, программа уже запущена. " + ex.Message, "1C-Backup");
            Shutdown(1);
            return;
        }
        DesktopTheme.Initialize();
        base.OnStartup(e);
        var window = new MainWindow { SmokeMode = smokeOutput is not null };
        MainWindow = window;
        if (StartInTray) { window.ShowActivated = false; window.ShowInTaskbar = false; }
        window.Show();
        if (smokeOutput is not null)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle,
                new Action(async () => await DesktopSmoke.RunAsync(smokeOutput)));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DiagnosticLog.Current.Write(DiagnosticEvent.ProcessStopped, values: new(Code: e.ApplicationExitCode));
        DesktopTheme.Shutdown();
        showRegistration?.Unregister(null);
        showEvent?.Dispose();
        instanceLock?.Dispose();
        base.OnExit(e);
    }
}
