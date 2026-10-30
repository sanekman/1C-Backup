using System.Runtime.InteropServices;

namespace SPBackup.Core;

/// <summary>Best-effort capacity visible to the executing account, including Windows share quotas.</summary>
internal static class BackupDiskSpace
{
    internal static long? AvailableBytes(string directory)
    {
        try
        {
            var existing = Path.GetFullPath(directory);
            while (!Directory.Exists(existing))
            {
                if (File.Exists(existing)) return null;
                var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(existing));
                if (parent is null || parent == existing) return null;
                existing = parent;
            }
            if (OperatingSystem.IsWindows())
            {
                // DriveInfo uses a drive root on Windows and cannot account for UNC or mounted-volume quotas.
                var path = Path.EndsInDirectorySeparator(existing) ? existing : existing + Path.DirectorySeparatorChar;
                return GetDiskFreeSpaceEx(path, out var available, out _, out _) ? (long)Math.Min(available, long.MaxValue) : null;
            }
            return new DriveInfo(existing).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { return null; }
    }

    internal static void RequireAvailable(string directory, long required, Func<string, long?> query)
    {
        var available = query(directory);
        if (available is >= 0 && available < required)
            throw new IOException($"Недостаточно места в хранилище: {directory}. Для готового архива нужно {Format(required)}, доступно {Format(available.Value)}. Освободите место или выберите другое хранилище. Прежние копии не удаляются для освобождения места.");
    }

    internal static string Format(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024):N2} ГиБ ({bytes:N0} байт)"
        : $"{bytes / (1024d * 1024):N2} МиБ ({bytes:N0} байт)";

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong totalFree);
}
