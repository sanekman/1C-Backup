using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace SPBackup.Core;

/// <summary>A private platform parameter file, outside the work directory and backup archive.</summary>
internal sealed class OneCStartupFile : IDisposable
{
    public string Path { get; }
    private readonly string directory;
    private bool ownsDirectory;
    private OneCStartupFile(string directory) { this.directory = directory; Path = System.IO.Path.Combine(directory,"startup.prm"); }
    public static async Task<OneCStartupFile> CreateAsync(IReadOnlyList<string> arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OneCBackup-ib-login-" + Guid.NewGuid().ToString("N"));
        var file = new OneCStartupFile(folder);
        try
        {
            if (Directory.Exists(folder) || File.Exists(folder)) throw new IOException("Временная папка входа уже существует.");
            if (OperatingSystem.IsWindows()) CreateWindowsDirectory(folder);
            else Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            file.ownsDirectory=true;
            if (File.GetAttributes(folder).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Папка входа 1С не должна быть ссылкой.");
            var parts=new List<string>();var valueExpected=false;
            foreach(var argument in arguments)
            {
                if(valueExpected) { parts.Add(Quote(argument));valueExpected=false; }
                else
                {
                    parts.Add(argument);
                    valueExpected=argument is "/F" or "/DumpIB" or "/RestoreIB" or "/Out" or "/N" or "/P";
                }
            }
            var text = string.Join(" ",parts);
            // 1C detects a Unicode parameter file by its BOM. GetACP can return 65001
            // for a UTF-8 apphost; UTF-8 without a BOM is interpreted as ANSI by 1C.
            var encoding = new UTF8Encoding(true,true);
            byte[] bytes;
            try
            {
                var preamble=encoding.GetPreamble();
                bytes=new byte[preamble.Length+encoding.GetByteCount(text)];
                preamble.CopyTo(bytes,0);
                encoding.GetBytes(text.AsSpan(),bytes.AsSpan(preamble.Length));
            }
            catch (EncoderFallbackException)
            { throw new InvalidDataException("Параметры запуска 1С содержат некорректные символы Юникода."); }
            try
            {
                var options = new FileStreamOptions { Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None,Options=FileOptions.Asynchronous };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode=UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using var stream=new FileStream(file.Path,options);
                await stream.WriteAsync(bytes,token); await stream.FlushAsync(token);
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
            return file;
        }
        catch { file.Dispose(); throw; }
    }
    private static string Quote(string value)
    {
        if (value.IndexOfAny(['\r','\n','\0'])>=0) throw new InvalidDataException("Параметры запуска 1С не должны содержать переводы строк или нулевой символ.");
        return "\""+value.Replace("\"","\"\"")+"\"";
    }
    [SupportedOSPlatform("windows")]
    private static void CreateWindowsDirectory(string folder)
    {
        var current=WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("Не определён пользователь Windows.");
        var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);acl.SetOwner(current);
        foreach (var sid in new[] { current,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null) }.DistinctBy(s=>s.Value))
            acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        new DirectoryInfo(folder).Create(acl);
    }
    public void Dispose()
    {
        if(!ownsDirectory)return;
        if (File.Exists(Path)) File.Delete(Path);
        if (Directory.Exists(directory)) Directory.Delete(directory);
    }
}
