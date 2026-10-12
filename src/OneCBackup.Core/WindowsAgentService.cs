using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OneCBackup.Core;

public static class WindowsAgentServiceDefinition
{
    public const string Name = "OneCBackupAgent";
    public static string ImagePath(string executable, string directory)
    {
        if (new[] { executable, directory }.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || path.Any(char.IsControl) || path.Contains('"')) || Path.GetFileName(executable) != "OneCBackup.Agent.exe")
            throw new ArgumentException("Укажите программу агента и полный путь рабочего профиля.");
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        // CommandLineToArgvW requires backslashes immediately before a closing quote to be doubled.
        var trailing = folder.Length - folder.TrimEnd('\\').Length;
        return $"\"{Path.GetFullPath(executable)}\" --service --data-dir \"{folder}{new string('\\', trailing)}\"";
    }
}

public sealed record WindowsAgentServiceInfo(uint State, string ImagePath, string Account)
{
    public bool Running => State is 2 or 4;
    public string Description => State switch { 1 => "Остановлена", 2 => "Запускается", 3 => "Останавливается", 4 => "Работает", _ => "Состояние " + State };
    public bool Matches(string executable, string directory) => string.Equals(ImagePath,
        WindowsAgentServiceDefinition.ImagePath(executable, directory), StringComparison.OrdinalIgnoreCase);
}

/// <summary>SCM registration uses native APIs: passwords never enter process command lines or files.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsAgentService
{
    private const uint Query = 5, All = 0xF01FF;
    private static IntPtr Manager(uint rights)
    {
        var handle = Native.OpenSCManager(null, null, rights);
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось открыть управление службами Windows.");
        return handle;
    }
    private static IntPtr Service(IntPtr manager, uint rights, bool optional = false)
    {
        var handle = Native.OpenService(manager, WindowsAgentServiceDefinition.Name, rights);
        if (handle == IntPtr.Zero && !(optional && Marshal.GetLastWin32Error() == 1060))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось открыть службу 1C-Backup.");
        return handle;
    }
    public static WindowsAgentServiceInfo? QueryInfo()
    {
        var manager = Manager(1);
        try
        {
            var service = Service(manager, Query, true); if (service == IntPtr.Zero) return null;
            try
            {
                if (!Native.QueryServiceStatus(service, out var status)) throw new Win32Exception(Marshal.GetLastWin32Error());
                Native.QueryServiceConfig(service, IntPtr.Zero, 0, out var size);
                if (size is 0 or > 65536) throw new Win32Exception(Marshal.GetLastWin32Error());
                var buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (!Native.QueryServiceConfig(service, buffer, size, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    var config = Marshal.PtrToStructure<ServiceConfig>(buffer);
                    return new(status.CurrentState, Marshal.PtrToStringUni(config.BinaryPath) ?? "", Marshal.PtrToStringUni(config.Account) ?? "");
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { Native.CloseServiceHandle(service); }
        }
        finally { Native.CloseServiceHandle(manager); }
    }
    public static void Start(string executable, string directory)
    {
        AssertOwned(executable, directory);
        var manager = Manager(1);
        try
        {
            var service = Service(manager, 0x10);
            try
            {
                if (!Native.StartService(service, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() != 1056)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось запустить службу. Проверьте пароль Windows и журнал агента.");
            }
            finally { Native.CloseServiceHandle(service); }
        }
        finally { Native.CloseServiceHandle(manager); }
    }
    public static void Stop(string executable, string directory)
    {
        AssertOwned(executable, directory);
        var manager = Manager(1);
        try
        {
            var service = Service(manager, 0x20);
            try
            {
                if (!Native.ControlService(service, 1, out _) && Marshal.GetLastWin32Error() != 1062)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось остановить службу.");
            }
            finally { Native.CloseServiceHandle(service); }
        }
        finally { Native.CloseServiceHandle(manager); }
    }
    public static void AssertOwned(string executable, string directory)
    {
        var info = QueryInfo();
        if (info is null || !info.Matches(executable, directory))
            throw new InvalidOperationException("Служба не относится к этой папке программы и рабочему профилю.");
    }
    public static void Install(string executable, string directory, string account, string ownerSid, string password)
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Для установки службы нужны права администратора.");
        var profile = WindowsServiceProfile.LoadAsync(directory).GetAwaiter().GetResult() ?? throw new InvalidDataException("Нет профиля службы.");
        if (profile.OwnerSid != ownerSid || ((SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier))).Value != ownerSid)
            throw new InvalidOperationException("Учётная запись службы должна совпадать с владельцем профиля.");
        if (string.IsNullOrEmpty(password) || password.Length > 512 || password.IndexOf('\0') >= 0)
            throw new ArgumentException("Введите пароль Windows, а не PIN.");
        var image = WindowsAgentServiceDefinition.ImagePath(executable, directory);
        if (!File.Exists(executable)) throw new FileNotFoundException("Агент не найден.");
        if (QueryInfo() is not null) throw new InvalidOperationException("Служба уже установлена. Для переустановки сначала удалите её из программы.");
        var granted = ServiceLogonRight.Ensure(ownerSid);
        IntPtr manager = IntPtr.Zero, service = IntPtr.Zero, secret = IntPtr.Zero;
        try
        {
            manager = Manager(3);
            secret = Marshal.StringToHGlobalUni(password);
            service = Native.CreateService(manager, WindowsAgentServiceDefinition.Name, "1C-Backup — резервное копирование", All,
                0x10, 2, 1, image, null, IntPtr.Zero, null, account, secret);
            if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось установить службу. Проверьте пароль учётной записи Windows.");
            // The owner may start/stop/query; configuration and deletion remain administrator-only.
            var descriptor = new RawSecurityDescriptor("D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x200B5;;;" + ownerSid + ")");
            var bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0);
            if (!Native.SetServiceObjectSecurity(service, 4, bytes)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!Native.StartService(service, 0, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Служба не запустилась. Проверьте пароль Windows и доступ к файлам.");
        }
        catch
        {
            if (service != IntPtr.Zero) Native.DeleteService(service);
            if (granted) { try { ServiceLogonRight.Remove(ownerSid); } catch { /* Keep the original installation error. */ } }
            throw;
        }
        finally
        {
            if (secret != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(secret);
            if (service != IntPtr.Zero) Native.CloseServiceHandle(service);
            if (manager != IntPtr.Zero) Native.CloseServiceHandle(manager);
        }
    }
    public static void Remove(string executable, string directory)
    {
        AssertOwned(executable, directory);
        var info = QueryInfo()!;
        if (info.State != 1) throw new InvalidOperationException("Перед удалением остановите службу и дождитесь завершения копирования.");
        var manager = Manager(1);
        try
        {
            var service = Service(manager, 0x10000);
            try { if (!Native.DeleteService(service)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось удалить службу."); }
            finally { Native.CloseServiceHandle(service); }
        }
        finally { Native.CloseServiceHandle(manager); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus
    { public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, SpecificExitCode, CheckPoint, WaitHint; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceConfig
    { public uint Type, Start, Error; public IntPtr BinaryPath, Group; public uint Tag; public IntPtr Dependencies, Account, DisplayName; }
    private static class Native
    {
        [DllImport("advapi32.dll", EntryPoint="OpenSCManagerW", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
        [DllImport("advapi32.dll", EntryPoint="OpenServiceW", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr OpenService(IntPtr manager, string name, uint access);
        [DllImport("advapi32.dll", EntryPoint="CreateServiceW", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr CreateService(IntPtr manager, string name, string display, uint access, uint type, uint start, uint error, string image, string? group, IntPtr tag, string? dependencies, string account, IntPtr password);
        [DllImport("advapi32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool CloseServiceHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);
        [DllImport("advapi32.dll", EntryPoint="QueryServiceConfigW", CharSet=CharSet.Unicode, SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool QueryServiceConfig(IntPtr service, IntPtr config, uint size, out uint needed);
        [DllImport("advapi32.dll", EntryPoint="StartServiceW", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool StartService(IntPtr service, uint count, IntPtr arguments);
        [DllImport("advapi32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);
        [DllImport("advapi32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool DeleteService(IntPtr service);
        [DllImport("advapi32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool SetServiceObjectSecurity(IntPtr service, uint information, byte[] descriptor);
    }
}

[SupportedOSPlatform("windows")]
internal static class ServiceLogonRight
{
    [StructLayout(LayoutKind.Sequential)] private struct Attributes
    { public uint Length; public IntPtr Root, Name; public uint Flags; public IntPtr Descriptor, Quality; }
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString
    { public ushort Length, MaximumLength; public IntPtr Buffer; }
    private static UnicodeString Right()
    { const string name = "SeServiceLogonRight"; return new() { Length=(ushort)(name.Length*2), MaximumLength=(ushort)((name.Length+1)*2), Buffer=Marshal.StringToHGlobalUni(name) }; }
    public static bool Ensure(string sid) => Change(sid, false);
    public static void Remove(string sid) => Change(sid, true);
    private static bool Change(string owner, bool remove)
    {
        var sid = new SecurityIdentifier(owner); var bytes = new byte[sid.BinaryLength]; sid.GetBinaryForm(bytes, 0);
        var attributes = new Attributes { Length=(uint)Marshal.SizeOf<Attributes>() };
        var status = LsaOpenPolicy(IntPtr.Zero, ref attributes, 0x810, out var policy);
        if (status != 0) throw new Win32Exception((int)LsaNtStatusToWinError(status));
        var right = Right();
        try
        {
            if (!remove)
            {
                status = LsaEnumerateAccountRights(policy, bytes, out var rights, out var count);
                if (status == 0)
                {
                    try
                    {
                        for (var i=0; i<count; i++)
                        {
                            var item = Marshal.PtrToStructure<UnicodeString>(IntPtr.Add(rights, i*Marshal.SizeOf<UnicodeString>()));
                            if (Marshal.PtrToStringUni(item.Buffer, item.Length/2)=="SeServiceLogonRight") return false;
                        }
                    }
                    finally { LsaFreeMemory(rights); }
                }
                else if (LsaNtStatusToWinError(status)!=2) throw new Win32Exception((int)LsaNtStatusToWinError(status));
            }
            status = remove ? LsaRemoveAccountRights(policy, bytes, false, new[]{right}, 1) : LsaAddAccountRights(policy, bytes, new[]{right}, 1);
            if (status != 0) throw new Win32Exception((int)LsaNtStatusToWinError(status));
            return true;
        }
        finally { Marshal.FreeHGlobal(right.Buffer); LsaClose(policy); }
    }
    [DllImport("advapi32.dll")] private static extern uint LsaOpenPolicy(IntPtr system, ref Attributes attributes, uint access, out IntPtr policy);
    [DllImport("advapi32.dll")] private static extern uint LsaEnumerateAccountRights(IntPtr policy, byte[] sid, out IntPtr rights, out uint count);
    [DllImport("advapi32.dll")] private static extern uint LsaAddAccountRights(IntPtr policy, byte[] sid, UnicodeString[] rights, uint count);
    [DllImport("advapi32.dll")] private static extern uint LsaRemoveAccountRights(IntPtr policy, byte[] sid, [MarshalAs(UnmanagedType.Bool)] bool all, UnicodeString[] rights, uint count);
    [DllImport("advapi32.dll")] private static extern uint LsaNtStatusToWinError(uint status);
    [DllImport("advapi32.dll")] private static extern uint LsaFreeMemory(IntPtr memory);
    [DllImport("advapi32.dll")] private static extern uint LsaClose(IntPtr policy);
}
