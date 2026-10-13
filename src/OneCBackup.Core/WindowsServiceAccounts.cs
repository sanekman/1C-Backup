using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OneCBackup.Core;

public sealed record WindowsServiceAccount(string Name, string Sid);

public static class ServiceAccountName
{
    public static string Normalize(string value, string machine)
    {
        if (value is null || value.Any(char.IsControl))
            throw new ArgumentException("Имя пользователя не должно содержать управляющие символы.");
        value = value.Trim();
        if (value.Length is 0 or > 512 || value.Any(char.IsControl) || value.Contains('"'))
            throw new ArgumentException("Укажите пользователя Windows: КОМПЬЮТЕР\\пользователь или ДОМЕН\\пользователь.");
        if (value.StartsWith(@".\", StringComparison.Ordinal)) value = machine + value[1..];
        else if (!value.Contains('\\') && !value.Contains('@')) value = machine + "\\" + value;
        if (value.EndsWith('\\') || value.Count(c => c == '\\') > 1 || value.StartsWith('\\'))
            throw new ArgumentException("Некорректное имя пользователя Windows.");
        return value;
    }
}

[SupportedOSPlatform("windows")]
public static class WindowsServiceAccounts
{
    public static WindowsServiceAccount Resolve(string name)
    {
        name = ServiceAccountName.Normalize(name, Environment.MachineName);
        uint sidSize = 0, domainSize = 0;
        LookupAccountName(null, name, null, ref sidSize, null, ref domainSize, out _);
        if (Marshal.GetLastWin32Error() != 122 || sidSize is 0 or > 256 || domainSize > 1024)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Пользователь Windows не найден. Проверьте имя и доступность домена.");
        var bytes = new byte[sidSize]; var domain = new StringBuilder((int)domainSize);
        if (!LookupAccountName(null, name, bytes, ref sidSize, domain, ref domainSize, out var use))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось проверить пользователя Windows.");
        var sid = new SecurityIdentifier(bytes, 0);
        if (use != 1 || sid.Value is "S-1-5-18" or "S-1-5-19" or "S-1-5-20")
            throw new ArgumentException("Выберите учётную запись пользователя, а не группу или системную службу.");
        return new(((NTAccount)sid.Translate(typeof(NTAccount))).Value, sid.Value);
    }
    public static IReadOnlyList<string> LocalUsers()
    {
        var result = new List<string>(); UIntPtr resume = UIntPtr.Zero; uint status;
        do
        {
            status = NetUserEnum(null, 1, 2, out var buffer, 65536, out var count, out _, ref resume);
            try
            {
                if (status is not (0 or 234)) throw new Win32Exception((int)status, "Не удалось получить список пользователей Windows.");
                if (status == 234 && count == 0) throw new InvalidDataException("Список пользователей не получен; введите имя вручную.");
                if (count > 4096 || result.Count + count > 4096) throw new InvalidDataException("Список пользователей слишком большой; введите имя вручную.");
                for (var i=0; i<count; i++)
                {
                    var item = Marshal.PtrToStructure<UserInfo>(IntPtr.Add(buffer, i * Marshal.SizeOf<UserInfo>()));
                    if ((item.Flags & 2) == 0 && Marshal.PtrToStringUni(item.Name) is { Length: > 0 } name)
                        result.Add(Environment.MachineName + "\\" + name);
                }
            }
            finally { if (buffer != IntPtr.Zero) NetApiBufferFree(buffer); }
        } while (status == 234);
        return result.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    public static void AssertPipeOwner(SafePipeHandle pipe, string ownerSid)
    {
        if (!GetNamedPipeServerProcessId(pipe, out var pid)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var process = OpenProcess(0x1000, false, pid);
        if (process == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!OpenProcessToken(process, 8, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                using var identity = new WindowsIdentity(token);
                if (identity.User?.Value != ownerSid) throw new UnauthorizedAccessException("Владелец канала управления не совпадает с владельцем заданий.");
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct UserInfo
    { public IntPtr Name, Password; public uint PasswordAge, Privilege; public IntPtr Home, Comment; public uint Flags; public IntPtr Script; }
    [DllImport("advapi32.dll", EntryPoint="LookupAccountNameW", CharSet=CharSet.Unicode, SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupAccountName(string? system, string name, byte[]? sid, ref uint sidSize, StringBuilder? domain, ref uint domainSize, out uint use);
    [DllImport("netapi32.dll", CharSet=CharSet.Unicode)] private static extern uint NetUserEnum(string? server, uint level, uint filter, out IntPtr buffer, uint preferred, out uint read, out uint total, ref UIntPtr resume);
    [DllImport("netapi32.dll")] private static extern uint NetApiBufferFree(IntPtr buffer);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("kernel32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
