using System.Runtime.InteropServices;

namespace SPBackup.Core;

/// <summary>A stable OS boot identity, independent of process restarts and wall-clock changes.</summary>
public static class SystemBootSession
{
    public static Guid ReadId()
    {
        Guid id;
        if (OperatingSystem.IsWindows())
        {
            const int systemBootEnvironmentInformation = 90;
            var status = NtQuerySystemInformation(systemBootEnvironmentInformation, out var information,
                Marshal.SizeOf<BootEnvironmentInformation>(), out _);
            if (status < 0) throw new IOException($"Не удалось определить загрузку Windows (0x{status:X8}).");
            id = information.BootIdentifier;
        }
        else if (OperatingSystem.IsLinux())
            id = Guid.Parse(File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim());
        else throw new PlatformNotSupportedException("Запуск копии при загрузке системы поддерживается в Windows и Linux.");
        if (id == Guid.Empty) throw new InvalidDataException("Система вернула пустой идентификатор загрузки.");
        return id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BootEnvironmentInformation
    {
        public Guid BootIdentifier;
        public uint FirmwareType;
        public ulong BootFlags;
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQuerySystemInformation(int informationClass,
        out BootEnvironmentInformation information, int informationLength, out int returnLength);
}

/// <summary>The host must hold the profile engine lease. Consume before execution to avoid replay after crashes.</summary>
public sealed class StartupBackupSchedule(string path, Func<Guid>? bootIdentity = null)
{
    private sealed record State(Guid BootId, IReadOnlyList<Guid> AttemptedJobs);
    private readonly AgentJsonFile<State> file = new(path);
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<bool> TryConsumeAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (jobId == Guid.Empty) throw new ArgumentException("Не указан идентификатор задания.", nameof(jobId));
        await gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bootId = (bootIdentity ?? SystemBootSession.ReadId)();
            if (bootId == Guid.Empty) throw new InvalidDataException("Не определена загрузка системы.");
            var state = await file.ReadAsync(cancellationToken);
            if (state is not null && (state.BootId == Guid.Empty || state.AttemptedJobs is null
                || state.AttemptedJobs.Contains(Guid.Empty) || state.AttemptedJobs.Distinct().Count() != state.AttemptedJobs.Count))
                throw new InvalidDataException("Повреждена история запусков при загрузке системы.");
            if (state?.BootId != bootId) state = new(bootId, []);
            if (state!.AttemptedJobs.Contains(jobId)) return false;
            await file.WriteAsync(state with { AttemptedJobs = state.AttemptedJobs.Append(jobId).ToArray() }, cancellationToken);
            return true;
        }
        finally { gate.Release(); }
    }
}
