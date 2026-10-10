using System.Diagnostics;

namespace OneCBackup.Core;

public sealed record OneCSettings
{
    public string ExecutablePath { get; init; } = "";
    public int TimeoutMinutes { get; init; } = 120;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(ExecutablePath) || !Path.IsPathFullyQualified(ExecutablePath))
            errors.Add("Укажите полный путь к 1cv8.exe.");
        if (TimeoutMinutes is < 1 or > 10080)
            errors.Add("Время ожидания выгрузки должно быть от 1 до 10080 минут.");
        return errors;
    }
}

public sealed record BackupProcess(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory);

public interface IBackupProcessRunner
{
    Task<int> RunAsync(BackupProcess command, CancellationToken cancellationToken);
}

public sealed class BackupProcessRunner : IBackupProcessRunner
{
    public async Task<int> RunAsync(BackupProcess command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(command.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = command.WorkingDirectory
        };
        foreach (var argument in command.Arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new IOException("Не удалось запустить платформу 1С.");
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            // Do not release the executor until the platform has stopped writing the dump.
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        return process.ExitCode;
    }
}

public interface IBackupExecutor
{
    Task<string> CreateAsync(BackupJob job, string workDirectory, CancellationToken cancellationToken);
}

/// <summary>Uses the platform's exclusive Designer dump; never copies an open 1CD file.</summary>
public sealed class OneCBackupExecutor(IBackupProcessRunner processRunner) : IBackupExecutor
{
    public async Task<string> CreateAsync(BackupJob job, string workDirectory, CancellationToken cancellationToken)
    {
        if (job.Kind != DatabaseKind.OneCFile) throw new NotSupportedException("Этот исполнитель поддерживает файловые базы 1С.");
        if (job.OneCMethod == OneCBackupMethod.FileArchive)
            return await new OneCFileArchiveExecutor().CreateAsync(job, workDirectory, cancellationToken);
        if (job.OneCMethod != OneCBackupMethod.PlatformDump)
            throw new InvalidDataException("Неизвестный способ резервного копирования 1С.");
        var settings = job.OneC ?? throw new InvalidOperationException("Укажите путь к платформе 1С в настройках задания.");
        var errors = settings.Validate();
        if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
        if (!File.Exists(settings.ExecutablePath)) throw new FileNotFoundException("Не найдена платформа 1С.", settings.ExecutablePath);
        var source = Path.GetFullPath(job.Source);
        if (!File.Exists(Path.Combine(source, "1Cv8.1CD")))
            throw new FileNotFoundException("В каталоге источника не найден файл 1Cv8.1CD.");
        Directory.CreateDirectory(workDirectory);
        var dump = Path.Combine(Path.GetFullPath(workDirectory), $"{job.Id:N}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.dt");
        var log = Path.Combine(Path.GetFullPath(workDirectory), "platform.log");
        var command = new BackupProcess(settings.ExecutablePath,
            ["DESIGNER", "/F", source, "/DumpIB", dump, "/Out", log,
             "/DisableStartupMessages", "/DisableStartupDialogs"], Path.GetFullPath(workDirectory));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(settings.TimeoutMinutes));
        int exitCode;
        try { exitCode = await processRunner.RunAsync(command, timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException($"Выгрузка 1С превысила {settings.TimeoutMinutes} мин. Журнал: {log}"); }
        cancellationToken.ThrowIfCancellationRequested();
        if (exitCode != 0) throw new IOException($"Платформа 1С завершилась с кодом {exitCode}. Журнал: {log}");
        if (!File.Exists(dump) || new FileInfo(dump).Length == 0)
            throw new InvalidDataException($"Платформа 1С не создала непустую выгрузку. Журнал: {log}");
        return dump;
    }
}
