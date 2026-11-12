using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SPBackup.Core;

/// <summary>A stable OS boot identity, independent of process restarts and wall-clock changes.</summary>
public static class SystemBootSession
{
    private static readonly object WindowsGate = new();
    private static Guid? windowsId;
    public static Guid ReadId()
    {
        Guid id;
        if (OperatingSystem.IsWindows())
        {
            lock (WindowsGate)
            {
                // Cache only a successful query. A transient failure must remain retryable.
                if (windowsId is null)
                {
                    const int systemBootEnvironmentInformation = 90;
                    var status = NtQuerySystemInformation(systemBootEnvironmentInformation, out var information,
                        Marshal.SizeOf<BootEnvironmentInformation>(), out _);
                    if (status < 0) throw new IOException($"Не удалось определить загрузку Windows (0x{status:X8}).");
                    windowsId = WindowsIdentity(information.BootIdentifier, ReadSystemStartTime());
                }
                id = windowsId.Value;
            }
        }
        else if (OperatingSystem.IsLinux())
            id = Guid.Parse(File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim());
        else throw new PlatformNotSupportedException("Запуск копии при загрузке системы поддерживается в Windows и Linux.");
        if (id == Guid.Empty) throw new InvalidDataException("Система вернула пустой идентификатор загрузки.");
        return id;
    }

    internal static Guid WindowsIdentity(Guid bootEntry, long systemProcessStartTime)
    {
        if (bootEntry == Guid.Empty || systemProcessStartTime <= 0)
            throw new InvalidDataException("Не определён системный процесс текущей загрузки Windows.");
        // BootIdentifier identifies the BCD entry, not a particular boot. PID 4 exists for
        // the kernel's lifetime; its captured CreateTime survives service/account changes
        // and clock corrections (PsGetProcessCreateTimeQuadPart documents this property).
        Span<byte> identity = stackalloc byte[24];
        bootEntry.TryWriteBytes(identity[..16]);
        BinaryPrimitives.WriteInt64LittleEndian(identity[16..], systemProcessStartTime);
        return new Guid(SHA256.HashData(identity).AsSpan(0, 16));
    }

    private static long ReadSystemStartTime()
    {
        const int maximum = 64 * 1024 * 1024;
        var size = 64 * 1024;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQuerySystemInformation(5, buffer, size, out var needed);
                if (status is unchecked((int)0xC0000004) or unchecked((int)0xC0000023))
                {
                    var next = Math.Max((long)size * 2, (long)needed + 4096);
                    if (next > maximum) break;
                    size = (int)next; continue;
                }
                if (status < 0) throw new IOException($"Не удалось прочитать системный процесс Windows (0x{status:X8}).");
                if (needed <= 0 || needed > size) throw new InvalidDataException("Некорректный размер списка процессов Windows.");
                var bytes = new byte[needed]; Marshal.Copy(buffer, bytes, 0, needed);
                return ParseSystemStartTime(bytes, IntPtr.Size);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new IOException("Не удалось получить стабильный список процессов Windows для определения загрузки.");
    }

    internal static long ParseSystemStartTime(ReadOnlySpan<byte> processes, int pointerSize)
    {
        if (pointerSize is not (4 or 8)) throw new ArgumentOutOfRangeException(nameof(pointerSize));
        // SYSTEM_PROCESS_INFORMATION: CreateTime at 0x20; UniqueProcessId follows
        // UNICODE_STRING and BasePriority. Never dereference pointers from this buffer.
        var processIdOffset = pointerSize == 8 ? 80 : 68;
        var minimum = processIdOffset + pointerSize;
        while (processes.Length >= minimum)
        {
            var pid = pointerSize == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(processes[processIdOffset..])
                : BinaryPrimitives.ReadUInt32LittleEndian(processes[processIdOffset..]);
            if (pid == 4)
            {
                var time = BinaryPrimitives.ReadInt64LittleEndian(processes[32..]);
                if (time <= 0) throw new InvalidDataException("Системный процесс Windows не содержит времени создания.");
                return time;
            }
            var next = BinaryPrimitives.ReadUInt32LittleEndian(processes);
            if (next == 0) break;
            if (next < minimum || next > processes.Length - minimum)
                throw new InvalidDataException("Некорректное смещение списка процессов Windows.");
            processes = processes[(int)next..];
        }
        throw new InvalidDataException("Системный процесс Windows не найден в списке процессов.");
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

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQuerySystemInformation(int informationClass,
        IntPtr information, int informationLength, out int returnLength);
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
