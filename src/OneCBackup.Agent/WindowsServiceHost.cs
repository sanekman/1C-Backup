using System.IO.Pipes;
using System.Runtime.Versioning;
using System.ServiceProcess;
using OneCBackup.Core;

[SupportedOSPlatform("windows")]
internal sealed class WindowsServiceHost(string directory, int poll) : ServiceBase
{
    private CancellationTokenSource? cancellation;
    private Task<int>? running;
    protected override void OnStart(string[] args)
    {
        var profile = WindowsServiceProfile.LoadAsync(directory).GetAwaiter().GetResult() ?? throw new InvalidDataException("Не настроен профиль службы.");
        profile.ApplyEnvironment();
        WindowsFtpCredentialStore.SetServiceCache(new(directory, profile.OwnerSid, profile.EffectiveServiceSid));
        cancellation = new();
        running = Task.Run(async () =>
        {
            return await new LocalAgentHost(directory, new DatabaseBackupExecutor(new BackupProcessRunner()), new WindowsFtpCredentialStore())
                .RunAsync(null, TimeSpan.FromSeconds(poll), _ => Task.CompletedTask, cancellation.Token);
        });
        _ = running.ContinueWith(task =>
        {
            ExitCode = task.IsCompletedSuccessfully && task.Result == 0 ? 0 : 1;
            if (task.Exception is not null) DiagnosticLog.Current.Write(DiagnosticEvent.ProcessError, DiagnosticLevel.Error, task.Exception);
            // Stop on a separate continuation after RunAsync has released the profile lease.
            try { Stop(); } catch (InvalidOperationException) { }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }
    protected override void OnStop()
    {
        cancellation?.Cancel();
        if (running is null) return;
        while (!running.IsCompleted) { RequestAdditionalTime(15000); Thread.Sleep(200); }
        try { ExitCode = running.GetAwaiter().GetResult(); } catch { ExitCode = 1; }
    }
    protected override void OnShutdown()
    {
        cancellation?.Cancel();
        try { running?.GetAwaiter().GetResult(); } catch { ExitCode = 1; }
    }
    protected override void Dispose(bool disposing)
    { if (disposing) cancellation?.Dispose(); base.Dispose(disposing); }
    public static int Run(string directory, int poll)
    {
        using var service = new WindowsServiceHost(directory, poll) { ServiceName = WindowsAgentServiceDefinition.Name,
            CanStop = true, CanShutdown = true, AutoLog = false };
        ServiceBase.Run(service); return service.ExitCode;
    }
    public static async Task<int> ManageAsync(string pipeName)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(pipeName, "^OneCBackup-Service-[a-f0-9]{32}$")) throw new ArgumentException("Некорректный канал управления.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        ServiceManagementResult result;
        try
        {
            var request = await ServiceManagementProtocol.ReadAsync<ServiceManagementRequest>(pipe, timeout.Token);
            if (!string.Equals(Path.GetFullPath(request.Executable), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Путь агента не совпадает с запущенной программой.");
            WindowsServiceAccounts.AssertPipeOwner(pipe.SafePipeHandle, request.OwnerSid);
            switch (request.Action)
            {
                case "install": WindowsAgentService.Install(request.Executable, request.Directory, request.Account, request.OwnerSid, request.Password ?? ""); break;
                case "remove": WindowsAgentService.Remove(request.Executable, request.Directory); break;
                default: throw new ArgumentException("Неизвестное действие службы.");
            }
            result = new(true, request.Action == "install" ? "Служба установлена и запускается автоматически при включении Windows." : "Служба удалена. Задания и копии сохранены.");
        }
        catch (Exception error) { result = new(false, error.Message); }
        await ServiceManagementProtocol.WriteAsync(pipe, result, timeout.Token);
        return result.Success ? 0 : 1;
    }
}
