using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace SPBackup.Core;

/// <summary>Each operation gets its own outbound-only logon; existing SMB sessions are untouched.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsNetworkShareStorage(FileBackupStorage files, NetworkCredential credential) : IBackupStorage
{
    private async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var username = credential.UserName; string? domain = null;
        var separator = username.IndexOf('\\');
        if (separator >= 0) { domain = username[..separator]; username = username[(separator + 1)..]; }
        if (string.IsNullOrWhiteSpace(username) || username.Any(char.IsControl) || credential.Password.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new InvalidDataException("Задайте корректные логин и пароль сетевого хранилища.");
        if (!LogonUser(username, domain, credential.Password, 9 /* NEW_CREDENTIALS */, 3 /* WINNT50 */, out var identity))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось подготовить отдельный сетевой вход.");
        using (identity)
            return await WindowsIdentity.RunImpersonatedAsync(identity, action);
    }
    private Task RunAsync(Func<Task> action, CancellationToken token) => RunAsync(async () => { await action(); return true; }, token);
    public async Task<StoredBackup> StoreAsync(string completedBackupPath, BackupJob job, CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(completedBackupPath);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await RunAsync(() => files.StoreOpenAsync(input, source, job, cancellationToken), cancellationToken);
    }
    public Task VerifyAsync(StoredBackup backup, BackupJob job, CancellationToken cancellationToken = default) =>
        RunAsync(() => files.VerifyAsync(backup, job, cancellationToken), cancellationToken);
    public Task CopyToAsync(StoredBackup backup, BackupJob job, Stream destination, CancellationToken cancellationToken = default) =>
        RunAsync(() => files.CopyToAsync(backup, job, destination, cancellationToken), cancellationToken);
    public Task DeleteAsync(StoredBackup backup, BackupJob job, CancellationToken cancellationToken = default) =>
        RunAsync(() => files.DeleteAsync(backup, job, cancellationToken), cancellationToken);
    internal Task<FileStream> OpenArchiveReadAsync(string path, CancellationToken token) => RunAsync(() =>
    {
        FileBackupPaths.RejectReparse(path);
        return Task.FromResult(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan));
    }, token);
    [DllImport("advapi32.dll", EntryPoint = "LogonUserW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUser(string username, string? domain, string password, int logonType, int provider, out SafeAccessTokenHandle token);
}
