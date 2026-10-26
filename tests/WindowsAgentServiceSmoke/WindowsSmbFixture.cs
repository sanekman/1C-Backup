using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

/// <summary>Owned loopback SMB shares: one permits the service account, one explicitly denies it.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsSmbFixture : IAsyncDisposable
{
    private readonly string id = Guid.NewGuid().ToString("N");
    private readonly string account;
    private readonly string serviceSid;
    private readonly string ownerAccount;
    private readonly SecurityIdentifier ownerSid;
    private readonly string name;
    private readonly string deniedName;
    private readonly string marker;
    private bool rootCreated;
    private bool disposed;

    public string Root { get; }
    public string BackingDirectory => Path.Combine(Root, "allowed");
    public string DeniedBackingDirectory => Path.Combine(Root, "denied");
    public string UncPath => @"\\" + Environment.MachineName + @"\" + name;
    public string DeniedUncPath => @"\\" + Environment.MachineName + @"\" + deniedName;

    private WindowsSmbFixture(string account, string serviceSid)
    {
        using var identity = WindowsIdentity.GetCurrent();
        ownerSid = identity.User ?? throw new InvalidOperationException("The isolated runner has no user SID.");
        ownerAccount = identity.Name;
        var selected = new SecurityIdentifier(serviceSid);
        if (selected.Equals(ownerSid) || !new NTAccount(account).Translate(typeof(SecurityIdentifier)).Equals(selected))
            throw new InvalidOperationException("The SMB fixture requires the actual distinct service user and matching SID.");
        this.account = account;
        this.serviceSid = selected.Value;
        name = "SPBackupServiceCI_" + id;
        deniedName = "SPBackupDeniedCI_" + id;
        marker = "Owned SP Backup service SMB fixture " + id;
        Root = Path.Combine(Path.GetTempPath(), "SPBackup-service-smb-" + id);
    }

    public static async Task<WindowsSmbFixture> CreateAsync(string account, string serviceSid)
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("ONECBACKUP_NATIVE_SERVICE_TEST") != "1"
            || !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("The SMB fixture requires explicit isolated administrator Windows service CI opt-in.");
        var fixture = new WindowsSmbFixture(account, serviceSid);
        try
        {
            fixture.CreateDirectories();
            await fixture.RunAsync("""
                if ((Get-Service -Name LanmanServer -ErrorAction Stop).Status -ne 'Running') {
                    throw 'LanmanServer must already be running; the fixture does not change services or network policy.'
                }
                foreach ($command in @('Get-SmbShare', 'New-SmbShare', 'Remove-SmbShare', 'Get-SmbShareAccess')) {
                    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) { throw "Required SMB command is unavailable: $command" }
                }
                foreach ($item in @($settings.shares)) {
                    if (Find-OwnedShare $item) { throw 'Refusing to reuse a pre-existing SMB share.' }
                }
                foreach ($item in @($settings.shares)) {
                    if ($item.allow) {
                        New-SmbShare -Name $item.name -Path $item.backing -Description $item.marker `
                            -FullAccess $settings.account -CachingMode None -ErrorAction Stop | Out-Null
                    } else {
                        New-SmbShare -Name $item.name -Path $item.backing -Description $item.marker `
                            -FullAccess $settings.ownerAccount -NoAccess $settings.account -CachingMode None -ErrorAction Stop | Out-Null
                    }
                    if (-not (Find-OwnedShare $item)) { throw 'The owned service SMB share was not created.' }
                    $rules = @(Get-SmbShareAccess -Name $item.name -ErrorAction Stop | Where-Object {
                        ([Security.Principal.NTAccount]$_.AccountName).Translate([Security.Principal.SecurityIdentifier]).Value -eq $settings.serviceSid
                    })
                    if ($rules.Count -ne 1 -or ($item.allow -and ($rules[0].AccessControlType -ne 'Allow' -or $rules[0].AccessRight -ne 'Full')) `
                        -or (-not $item.allow -and $rules[0].AccessControlType -ne 'Deny')) {
                        throw 'The service SID does not have the expected explicit SMB permission.'
                    }
                }
                """);
            return fixture;
        }
        catch (Exception initializationError)
        {
            try { await fixture.DisposeAsync(); }
            catch (Exception cleanupError)
            { throw new AggregateException("Service SMB initialization and owned-resource cleanup failed.", initializationError, cleanupError); }
            throw;
        }
    }

    private void CreateDirectories()
    {
        if (Directory.Exists(Root) || File.Exists(Root)) throw new IOException("Refusing to reuse an existing service SMB fixture directory.");
        var service = new SecurityIdentifier(serviceSid);
        new DirectoryInfo(Root).Create(Permissions(service, FileSystemRights.ReadAndExecute));
        rootCreated = true;
        File.WriteAllText(Path.Combine(Root, "fixture-owner.txt"), marker);
        foreach (var path in new[] { BackingDirectory, DeniedBackingDirectory })
        {
            new DirectoryInfo(path).Create(Permissions(service, FileSystemRights.Modify));
            var acl = new DirectoryInfo(path).GetAccessControl();
            var rules = acl.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Where(rule => rule.IdentityReference.Equals(service)).ToArray();
            if (!acl.AreAccessRulesProtected || rules.Length != 1 || rules[0].AccessControlType != AccessControlType.Allow
                || (rules[0].FileSystemRights & FileSystemRights.Modify) != FileSystemRights.Modify)
                throw new InvalidOperationException("The service SMB backing folder lacks the expected explicit NTFS modify permission.");
        }
    }

    private DirectorySecurity Permissions(SecurityIdentifier service, FileSystemRights serviceRights)
    {
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false); acl.SetOwner(ownerSid);
        foreach (var sid in new[] { ownerSid, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) }.Distinct())
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(service, serviceRights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return acl;
    }

    /// <summary>The runner reads a service-created archive locally; it never borrows the service's SMB credentials.</summary>
    public string LocalPath(string uncFile)
    {
        var full = Path.GetFullPath(uncFile);
        var prefix = Path.TrimEndingDirectorySeparator(UncPath) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The archive does not belong to the allowed service SMB fixture.");
        return Path.Combine(BackingDirectory, Path.GetRelativePath(UncPath, full));
    }

    private async Task RunAsync(string command)
    {
        var parameters = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            account, serviceSid, ownerAccount,
            shares = new[]
            {
                new { name, backing = BackingDirectory, marker = marker + " allowed", allow = true },
                new { name = deniedName, backing = DeniedBackingDirectory, marker = marker + " denied", allow = false }
            }
        })));
        // Only base64 JSON data crosses into PowerShell; accounts and paths never become shell code.
        var script = "$ErrorActionPreference = 'Stop'\n$settings = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"
            + parameters + "')) | ConvertFrom-Json\n" + """
            function Find-OwnedShare($item) {
                $share = Get-SmbShare -Name $item.name -ErrorAction SilentlyContinue
                if ($share -and ($share.Path -ine $item.backing -or $share.Description -ne $item.marker)) {
                    throw 'Refusing to modify a share whose path/ownership marker does not match this fixture.'
                }
                return $share
            }
            """ + "\n" + command;
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var process = Process.Start(start) ?? throw new IOException("The owned service SMB controller did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10));
            if (process.ExitCode != 0) throw new IOException("The owned service SMB controller failed: " + await stderr + "\n" + await stdout);
        }
        catch
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        await RunAsync("""
            foreach ($item in @($settings.shares)) {
                if (Find-OwnedShare $item) {
                    Remove-SmbShare -Name $item.name -Force -Confirm:$false -ErrorAction Stop
                }
                if (Find-OwnedShare $item) { throw 'The owned service SMB share remained registered after removal.' }
            }
            """);
        if (rootCreated && Directory.Exists(Root))
        {
            if ((File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0
                || File.ReadAllText(Path.Combine(Root, "fixture-owner.txt")) != marker)
                throw new InvalidOperationException("Refusing to remove a service SMB fixture directory whose ownership marker changed.");
            Directory.Delete(Root, recursive: true);
        }
        disposed = true;
    }
}
