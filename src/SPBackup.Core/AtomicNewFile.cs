using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SPBackup.Core;

/// <summary>Publishes a complete staging file without replacing a concurrent writer's result.</summary>
internal static class AtomicNewFile
{
    internal static void Publish(string temporary, string destination)
    {
        if (!OperatingSystem.IsLinux())
        {
            File.Move(temporary, destination, overwrite: false);
            return;
        }
        // Linux rename() can replace a destination created after a managed existence check.
        // Both names are in the destination directory, so RENAME_NOREPLACE publishes in one
        // filesystem operation and refuses files, directories and dangling links atomically.
        const int currentDirectory = -100;
        const uint noReplace = 1;
        if (RenameAt2(currentDirectory, temporary, currentDirectory, destination, noReplace) != 0)
        {
            var error = new Win32Exception(Marshal.GetLastPInvokeError());
            throw new IOException("Не удалось сохранить новый файл без замены существующего: " + error.Message, error);
        }
    }

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameAt2(int oldDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        int newDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, uint flags);
}
