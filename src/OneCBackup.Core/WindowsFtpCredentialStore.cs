using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;

namespace OneCBackup.Core;

/// <summary>Generic credentials belong to the current Windows user; never written to JSON.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFtpCredentialStore : IFtpCredentialStore
{
    private readonly string prefix;
    private static WindowsServiceCredentialCache? serviceCache;
    public static void SetServiceCache(WindowsServiceCredentialCache? cache) => serviceCache = cache;
    private bool HasServiceCache => (prefix is "FTP" or "Archive" or "WebDAV" or "GoogleDrive" or "Notifications" or "RemoteGateway") && serviceCache is not null;
    public async Task ExportForServiceAsync(string key, CancellationToken token = default)
    {
        var value = await GetAsync(key, token);
        if (HasServiceCache) serviceCache!.Save(prefix, key, value);
    }
    public static async Task ExportProfileForServiceAsync(IReadOnlyList<BackupJob> jobs, IReadOnlyList<BackupRecord> copies)
    {
        var ftp = new WindowsFtpCredentialStore(); var archive = new WindowsFtpCredentialStore("Archive");
        var dav = new WindowsFtpCredentialStore("WebDAV");
        var google = new WindowsFtpCredentialStore("GoogleDrive");
        foreach (var key in jobs.SelectMany(job => job.StorageJobs).Where(job => job.StorageKind == StorageKind.GoogleDrive)
            .Select(job => job.GoogleDrive!.CredentialKey).Distinct(StringComparer.Ordinal))
            await google.ExportForServiceAsync(key);
        foreach (var key in jobs.SelectMany(job => job.StorageJobs).Where(job => job.StorageKind == StorageKind.Ftp)
            .Select(job => job.Ftp?.CredentialKey).OfType<string>().Distinct()) await ftp.ExportForServiceAsync(key);
        foreach (var key in jobs.Where(job => job.Kind == DatabaseKind.WebDav).Select(job => job.WebDav?.CredentialKey)
            .OfType<string>().Distinct()) await dav.ExportForServiceAsync(key);
        foreach (var key in jobs.Select(job => job.ArchivePasswordKey).Concat(copies.Select(copy => copy.Backup.ArchivePasswordKey))
            .OfType<string>().Distinct()) await archive.ExportForServiceAsync(key);
    }
    public WindowsFtpCredentialStore() : this("FTP") { }
    internal WindowsFtpCredentialStore(string prefix) => this.prefix = prefix;
    private const uint Generic = 1;
    private const int MaximumBlobSize = 2560;
    private string Target(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 512 || key.Any(char.IsControl))
            throw new ArgumentException("Идентификатор учётных данных должен содержать от 1 до 512 символов без управляющих символов.");
        return "OneCBackup/" + prefix + "/" + key;
    }

    public Task<NetworkCredential> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = Target(key);
        if (HasServiceCache && serviceCache!.PreferCache)
            return Task.FromResult(serviceCache.Read(prefix, key) ?? throw new InvalidOperationException("Не экспортирован пароль для службы: " + key));
        if (!Native.CredRead(Target(key), Generic, 0, out var pointer))
        {
            var code = Marshal.GetLastWin32Error();
            if (HasServiceCache && serviceCache!.Read(prefix, key) is { } cached) return Task.FromResult(cached);
            if (code == 1168) throw new InvalidOperationException("Не сохранены учётные данные FTP: " + key);
            throw new Win32Exception(code, "Не удалось прочитать учётные данные FTP из Windows.");
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.BlobSize > MaximumBlobSize || credential.BlobSize % 2 != 0)
                throw new InvalidOperationException("Некорректный формат учётных данных FTP.");
            var bytes = new byte[(int)credential.BlobSize];
            try
            {
                if (bytes.Length != 0) Marshal.Copy(credential.Blob, bytes, 0, bytes.Length);
                var value = new NetworkCredential(Marshal.PtrToStringUni(credential.Username) ?? "", Encoding.Unicode.GetString(bytes));
                return Task.FromResult(value);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { Native.CredFree(pointer); }
    }

    public void Save(string key, string username, string password)
    {
        var target = Target(key);
        if (string.IsNullOrWhiteSpace(username) || username.Length > 512 || username.Any(char.IsControl))
            throw new ArgumentException("Укажите логин FTP длиной до 512 символов.");
        if (password.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException("Пароль FTP не должен содержать переводы строк или нулевые символы.");
        var bytes = Encoding.Unicode.GetBytes(password);
        IntPtr blob = IntPtr.Zero, name = IntPtr.Zero, user = IntPtr.Zero;
        try
        {
            if (bytes.Length > MaximumBlobSize) throw new ArgumentException("Пароль FTP слишком длинный для диспетчера учётных данных Windows.");
            name = Marshal.StringToCoTaskMemUni(target);
            user = Marshal.StringToCoTaskMemUni(username);
            if (bytes.Length != 0) { blob = Marshal.AllocHGlobal(bytes.Length); Marshal.Copy(bytes, 0, blob, bytes.Length); }
            var credential = new NativeCredential { Type = Generic, TargetName = name, Username = user,
                Blob = blob, BlobSize = (uint)bytes.Length, Persist = 2 /* local machine, current user */ };
            if (!Native.CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось сохранить учётные данные FTP в Windows.");
            if (HasServiceCache) serviceCache!.Save(prefix, key, new NetworkCredential(username, password));
        }
        finally
        {
            if (blob != IntPtr.Zero)
            {
                for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(blob, i, 0);
                Marshal.FreeHGlobal(blob);
            }
            if (name != IntPtr.Zero) Marshal.FreeCoTaskMem(name);
            if (user != IntPtr.Zero) Marshal.FreeCoTaskMem(user);
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>Explicit deletion only; jobs may share a credential reference.</summary>
    public bool Delete(string key)
    {
        if (HasServiceCache) serviceCache!.Delete(prefix, key);
        if (Native.CredDelete(Target(key), Generic, 0)) return true;
        var code = Marshal.GetLastWin32Error();
        if (code == 1168) return false;
        throw new Win32Exception(code, "Не удалось удалить учётные данные FTP из Windows.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags, Type;
        public IntPtr TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes, TargetAlias, Username;
    }
    private static class Native
    {
        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredWrite(ref NativeCredential credential, uint flags);
        [DllImport("advapi32.dll")]
        public static extern void CredFree(IntPtr pointer);
        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredDelete(string target, uint type, uint flags);
    }
}
