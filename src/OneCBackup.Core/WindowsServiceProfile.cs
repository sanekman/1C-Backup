using System.Net;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OneCBackup.Core;

public sealed record WindowsServiceProfile(string OwnerSid, string UserProfile, string AppData, string LocalAppData)
{
    public static string FileName => "windows-service.json";
    public void Validate()
    {
        if (!Regex.IsMatch(OwnerSid ?? "", @"^S-1-\d+(?:-\d+){1,15}$") || OwnerSid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20")
            throw new InvalidDataException("Для службы нужна учётная запись пользователя Windows.");
        if (new[] { UserProfile, AppData, LocalAppData }.Any(path => path is null || !Path.IsPathFullyQualified(path) || path.Any(char.IsControl)))
            throw new InvalidDataException("Некорректные папки профиля службы.");
    }
    public static async Task<WindowsServiceProfile?> LoadAsync(string directory, CancellationToken token = default)
    {
        var value = await new AgentJsonFile<WindowsServiceProfile>(Path.Combine(directory, FileName)).ReadAsync(token);
        value?.Validate(); return value;
    }
    public Task SaveAsync(string directory, CancellationToken token = default)
    { Validate(); return new AgentJsonFile<WindowsServiceProfile>(Path.Combine(directory, FileName)).WriteAsync(this, token); }
    [SupportedOSPlatform("windows")]
    public void AssertCurrentUser()
    {
        Validate();
        if (WindowsIdentity.GetCurrent().User?.Value != OwnerSid)
            throw new InvalidOperationException("Служба должна работать под учётной записью владельца заданий. Проверьте вход службы в services.msc.");
    }
    [SupportedOSPlatform("windows")]
    public void ApplyEnvironment()
    {
        AssertCurrentUser();
        Environment.SetEnvironmentVariable("USERPROFILE", UserProfile);
        Environment.SetEnvironmentVariable("APPDATA", AppData);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", LocalAppData);
        // libpq reads this explicitly; it must not use the service's default system profile.
        Environment.SetEnvironmentVariable("PGPASSFILE", Path.Combine(AppData, "postgresql", "pgpass.conf"));
    }
}

/// <summary>DPAPI machine encryption plus an owner/SYSTEM/admin-only ACL; never a plaintext export.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceCredentialCache
{
    private readonly string directory, sid;
    private sealed record Secret(string Username, string Password);
    public WindowsServiceCredentialCache(string profile, string ownerSid)
    {
        sid = ownerSid;
        if (WindowsIdentity.GetCurrent().User?.Value != ownerSid) throw new UnauthorizedAccessException("Другой владелец профиля службы.");
        directory = Path.Combine(Path.GetFullPath(profile), "service-secrets");
    }
    private byte[] Entropy(string prefix, string key) => SHA256.HashData(Encoding.UTF8.GetBytes("OneCBackup.Service.v1\0" + sid + "\0" + prefix + "\0" + key));
    private string FilePath(string prefix, string key) => Path.Combine(directory, Convert.ToHexString(Entropy(prefix, key)).ToLowerInvariant() + ".bin");
    private void SecureDirectory()
    {
        Directory.CreateDirectory(directory);
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Папка паролей службы не должна быть ссылкой.");
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(new SecurityIdentifier(sid));
        foreach (var account in new[] { new SecurityIdentifier(sid), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(account, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(acl);
    }
    public void Save(string prefix, string key, NetworkCredential credential)
    {
        SecureDirectory();
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Secret(credential.UserName, credential.Password));
        var file = FilePath(prefix, key); var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var encrypted = ProtectedData.Protect(plain, Entropy(prefix, key), DataProtectionScope.LocalMachine);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(encrypted); stream.Flush(true); }
            File.Move(temporary, file, true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public NetworkCredential? Read(string prefix, string key)
    {
        var file = FilePath(prefix, key); if (!File.Exists(file)) return null;
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint) || File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)
            || new FileInfo(file).Length > 16384) throw new InvalidDataException("Некорректный файл пароля службы.");
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(file), Entropy(prefix, key), DataProtectionScope.LocalMachine);
        try
        {
            var secret = JsonSerializer.Deserialize<Secret>(plain) ?? throw new InvalidDataException("Некорректный пароль службы.");
            return new NetworkCredential(secret.Username, secret.Password);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Delete(string prefix, string key)
    { var file = FilePath(prefix, key); if (File.Exists(file)) File.Delete(file); }
}
