using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace OneCBackup.Core;

/// <summary>Private libpq password file for one operation, outside the archived work directory.</summary>
internal sealed class PostgreSqlConnectionEnvironment : IDisposable
{
    private string? directory;
    private string? passwordFile;
    public IReadOnlyDictionary<string, string?>? Environment { get; private set; }

    public static async Task<PostgreSqlConnectionEnvironment> OpenAsync(BackupJob job,
        IPostgreSqlCredentialStore? credentials, CancellationToken token)
    {
        var result = new PostgreSqlConnectionEnvironment();
        var settings = job.PostgreSql!;
        if (settings.CredentialKey is null) return result; // Keep existing libpq authentication unchanged.
        if (credentials is null)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Для сохранённого пароля PostgreSQL нужно хранилище учётных данных.");
            credentials = new WindowsPostgreSqlCredentialStore();
        }
        var value = await credentials.GetAsync(settings.CredentialKey, token);
        token.ThrowIfCancellationRequested();
        if (!string.Equals(value.UserName, settings.Username, StringComparison.Ordinal))
            throw new InvalidDataException("Пользователь PostgreSQL изменён. Сохраните пароль заново.");
        static string Field(string text)
        {
            if (string.IsNullOrEmpty(text) || text == "*" || text.IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw new InvalidDataException("Для сохранённого пароля PostgreSQL укажите конкретные сервер, базу и пользователя без переводов строк.");
            return text.Replace("\\", "\\\\").Replace(":", "\\:");
        }
        var password = value.Password;
        if (password.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new InvalidDataException("Пароль PostgreSQL не должен содержать переводы строк или нулевые символы.");
        var line = Field(settings.Host) + ":" + settings.Port.ToString(CultureInfo.InvariantCulture) + ":"
            + Field(job.Source) + ":" + Field(settings.Username) + ":"
            + password.Replace("\\", "\\\\").Replace(":", "\\:") + "\n";
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "OneCBackup-pgpass-" + Guid.NewGuid().ToString("N"));
            if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Временная папка подключения уже существует.");
            if (OperatingSystem.IsWindows()) CreatePrivateWindowsDirectory(path);
            else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            result.directory = path;
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Временная папка подключения не должна быть ссылкой.");
            var file = Path.Combine(path, "pgpass.conf");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var output = new FileStream(file, options))
            {
                result.passwordFile = file;
                var bytes = Encoding.UTF8.GetBytes(line);
                try { await output.WriteAsync(bytes, token); }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
            }
            token.ThrowIfCancellationRequested();
            result.Environment = new Dictionary<string, string?>
            {
                ["PGPASSFILE"] = file, ["PGPASSWORD"] = null, ["PGSERVICE"] = null,
                ["PGSERVICEFILE"] = null, ["PGHOSTADDR"] = null
            };
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    [SupportedOSPlatform("windows")]
    private static void CreatePrivateWindowsDirectory(string path)
    {
        var sid = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("Не определён пользователь Windows.");
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false); acl.SetOwner(sid);
        foreach (var account in new[] { sid, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(account, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(acl); // ACL is applied at creation, before any secret is written.
    }

    public void Dispose()
    {
        if (passwordFile is not null) File.Delete(passwordFile);
        if (directory is not null) Directory.Delete(directory); // Never recursively remove unrelated files.
        passwordFile = directory = null;
    }
}
