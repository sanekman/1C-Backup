using System.Net;
using System.Text.Json;
using OneCBackup.Core;

var diagnosticExitCode = 0;
try
{
    var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OneCBackup");
    Guid? runJob = null;
    var command = "run";
    var poll = 30;
    var quiet = false;
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--data-dir" when i + 1 < args.Length: directory = args[++i]; break;
            case "--run-job" when i + 1 < args.Length: runJob = Guid.Parse(args[++i]); break;
            case "--poll-seconds" when i + 1 < args.Length: poll = int.Parse(args[++i]); break;
            case "--run": command = "run"; break;
            case "--quiet": quiet = true; break;
            case "--stop": command = "stop"; break;
            case "--status": command = "status"; break;
            case "--help":
                Console.WriteLine("OneCBackup.Agent [--run | --run-job GUID | --stop | --status] [--data-dir PATH] [--poll-seconds 1..3600]");
                return 0;
            default: throw new ArgumentException("Неизвестный или неполный аргумент: " + args[i]);
        }
    }
    if (!Path.IsPathFullyQualified(directory) || poll is < 1 or > 3600)
        throw new ArgumentException("Укажите полный путь рабочего каталога и интервал 1..3600 секунд.");
    // --status is polled by the GUI: do not create a new process log every few seconds.
    if (command != "status")
    {
        DiagnosticLog.Start(directory, DiagnosticComponent.Agent, typeof(Program).Assembly);
        AppDomain.CurrentDomain.UnhandledException += (_, error) => DiagnosticLog.Current.Write(DiagnosticEvent.UnhandledException, DiagnosticLevel.Error, error.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, error) => DiagnosticLog.Current.Write(DiagnosticEvent.UnobservedTaskException, DiagnosticLevel.Error, error.Exception);
    }
    var control = new LocalAgentControlStore(directory);
    if (command is "stop" or "status")
    {
        var state = await control.ReadAsync();
        if (command == "status")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { state, engineBusy = new BackupEngineCoordinator(directory).IsHeldElsewhere() }));
            return 0;
        }
        if (state is null || state.Phase is not (LocalAgentPhase.Running or LocalAgentPhase.Stopping) ||
            !new BackupEngineCoordinator(directory).IsHeldElsewhere())
            throw new InvalidOperationException("Фоновый агент не работает.");
        DiagnosticLog.Current.Write(DiagnosticEvent.AgentStopRequested);
        await control.RequestStopAsync(state.SessionId);
        Console.WriteLine("Запрошена остановка агента. Дождитесь завершения текущей операции.");
        return 0;
    }
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    IFtpCredentialStore credentials = OperatingSystem.IsWindows() ? new WindowsFtpCredentialStore() : new UnsupportedCredentials();
    var result = await new LocalAgentHost(directory, new DatabaseBackupExecutor(new BackupProcessRunner()), credentials)
        .RunAsync(runJob, TimeSpan.FromSeconds(poll), run =>
        {
            if (!quiet) Console.WriteLine($"{run.JobName}: {run.StatusDescription}. {run.Details}");
            return Task.CompletedTask;
        }, cancellation.Token);
    diagnosticExitCode = result; return result;
}
catch (Exception ex) { diagnosticExitCode = 1; DiagnosticLog.Current.Write(DiagnosticEvent.ProcessError, DiagnosticLevel.Error, ex); Console.Error.WriteLine(ex.Message); return 1; }
finally { DiagnosticLog.Current.Write(DiagnosticEvent.ProcessStopped, values: new(Code: diagnosticExitCode)); }

sealed class UnsupportedCredentials : IFtpCredentialStore
{
    public Task<NetworkCredential> GetAsync(string key, CancellationToken cancellationToken = default) =>
        throw new PlatformNotSupportedException("Учётные данные FTP этого агента доступны только в Windows.");
}
