using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

[SupportedOSPlatform("windows")]
internal static class WindowsAgentProcessLifetime
{
    private static readonly object Gate = new();
    // The agent itself belongs to this job. Keep its only non-inheritable handle
    // rooted until process exit: disposing it while alive would terminate us too.
    private static SafeFileHandle? lifetimeJob;

    public static void Initialize()
    {
        lock (Gate)
        {
            if (lifetimeJob is not null) return;
            var job = CreateJobObject(IntPtr.Zero, null);
            try
            {
                if (job.IsInvalid) throw Failure("создание группы процессов");
                var limits = new ExtendedLimitInformation
                {
                    BasicLimitInformation = new BasicLimitInformation { LimitFlags = 0x2000 } // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                };
                if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
                    throw Failure("настройка завершения дочерних процессов");
                // Join before any backup utility starts, so future children inherit
                // membership without a Process.Start -> AssignProcess crash window.
                if (!AssignProcessToJobObject(job, GetCurrentProcess()))
                    throw Failure("подключение агента к группе процессов");
                lifetimeJob = job;
            }
            catch
            {
                // Assignment has not succeeded; closing the job cannot terminate us.
                job.Dispose();
                throw;
            }
        }
    }

    private static Win32Exception Failure(string operation)
    {
        var code = Marshal.GetLastWin32Error();
        return new Win32Exception(code,
            $"Не удалось включить защиту дочерних процессов агента: {operation} (ошибка Windows {code}). "
            + "Перезапустите агент. Если ошибка повторится, выгрузите диагностику.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass,
        ref ExtendedLimitInformation information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
