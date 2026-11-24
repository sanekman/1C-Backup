param([Parameter(Mandatory)][string]$Installation, [Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or (Get-Service LanmanServer).Status -ne 'Running') {
    throw 'The historical UNC fixture requires an isolated Windows runner with SMB already enabled.'
}

$id = [Guid]::NewGuid().ToString('N')
$lab = Join-Path $env:RUNNER_TEMP "spbackup-unc-$id"
$backing = Join-Path $lab 'backing'
$share = "SPBackupFormerCi_$id"
$marker = "Owned historical UNC fixture $id"
$unc = "\\$env:COMPUTERNAME\$share"
$name = 'spbu' + $id.Substring(0, 10)
$account = "$env:COMPUTERNAME\$name"
$runner = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$secret = ConvertTo-SecureString ("Un!8a-" + [Guid]::NewGuid().ToString('N')) -AsPlainText -Force
$user = $null
$batchRightGranted = $false
$report = Join-Path $lab 'report'
$stage = 'prepare'
New-Item -ItemType Directory -Path $backing,$report,$Output | Out-Null

function Remove-OwnedShare {
    $existing = Get-SmbShare -Name $share -ErrorAction SilentlyContinue
    if (!$existing) { return }
    if ($existing.Path -ine $backing -or $existing.Description -ne $marker) { throw 'Unexpected SMB share ownership.' }
    Remove-SmbShare -Name $share -Force -Confirm:$false
}

function Invoke-Probe([string]$Phase) {
    $errors = Join-Path $lab "$Phase.stderr"
    $completion = Join-Path $lab "$Phase.exit"
    $values = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((@{
        probe=$exe; installation=$Installation; report=$report; lab=$lab; phase=$Phase; errors=$errors; completion=$completion
    } | ConvertTo-Json -Compress)))
    $script = '$v = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(''' + $values + ''')) | ConvertFrom-Json' + [Environment]::NewLine + @'
$env:GITHUB_ACTIONS = 'true'
& $v.probe $v.installation $v.report $v.phase $v.lab 2> $v.errors
$code = $LASTEXITCODE
[IO.File]::WriteAllText($v.completion, [string]$code)
exit $code
'@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
    $shell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
    $taskName = "SPBackup-UNC-$id-$Phase"
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) { throw 'Refusing to replace a pre-existing task.' }
    $action = New-ScheduledTaskAction -Execute $shell -Argument "-NoProfile -NonInteractive -EncodedCommand $encoded" -WorkingDirectory $lab
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 2) -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
    Register-ScheduledTask -TaskName $taskName -Action $action -Settings $settings -User $account -Password (ConvertFrom-SecureString $secret -AsPlainText) -RunLevel Limited | Out-Null
    try {
        $started = Get-Date
        Start-ScheduledTask -TaskName $taskName
        $deadline = [DateTime]::UtcNow.AddSeconds(120)
        do {
            $task = Get-ScheduledTask -TaskName $taskName
            if ($task.State -eq 'Ready' -and (Test-Path -LiteralPath $completion)) { break }
            $info = Get-ScheduledTaskInfo -TaskName $taskName
            if ($task.State -eq 'Ready' -and $info.LastRunTime -ge $started.AddSeconds(-2)) { break }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $deadline)
        $last = (Get-ScheduledTaskInfo -TaskName $taskName).LastTaskResult
        if (!(Test-Path -LiteralPath $completion) -or (Get-Content -LiteralPath $completion -Raw) -ne '0' -or $last -ne 0) {
            if (Test-Path -LiteralPath $errors) { Get-Content -LiteralPath $errors }
            throw ("UNC probe failed in $Phase with task result 0x{0:X8}." -f $last)
        }
    }
    finally {
        if ((Get-ScheduledTask -TaskName $taskName).State -eq 'Running') { Stop-ScheduledTask -TaskName $taskName }
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    }
}

try {
    if (Get-SmbShare -Name $share -ErrorAction SilentlyContinue) { throw 'Refusing to replace a pre-existing share.' }
    $user = New-LocalUser -Name $name -Password $secret -Description 'Disposable SP Backup UNC acceptance'
    $users = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
    if (!((Get-LocalGroupMember -SID $users).SID.Value -contains $user.SID.Value)) {
        Add-LocalGroupMember -SID $users -Member $account
    }
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
public static class UncFixtureBatchLogon {
    [StructLayout(LayoutKind.Sequential)] struct Attributes { public uint Length; public IntPtr Root,Name; public uint Flags; public IntPtr Descriptor,Quality; }
    [StructLayout(LayoutKind.Sequential)] struct UnicodeString { public ushort Length,MaximumLength; public IntPtr Buffer; }
    public static void Change(string value,bool remove) {
        var sid=new SecurityIdentifier(value); var bytes=new byte[sid.BinaryLength];sid.GetBinaryForm(bytes,0);
        var attributes=new Attributes{Length=(uint)Marshal.SizeOf<Attributes>()};
        var status=LsaOpenPolicy(IntPtr.Zero,ref attributes,0x810,out var policy);
        if(status!=0)throw new Win32Exception((int)LsaNtStatusToWinError(status));
        const string name="SeBatchLogonRight";
        var right=new UnicodeString{Length=(ushort)(name.Length*2),MaximumLength=(ushort)((name.Length+1)*2),Buffer=Marshal.StringToHGlobalUni(name)};
        try {
            status=remove?LsaRemoveAccountRights(policy,bytes,false,new[]{right},1):LsaAddAccountRights(policy,bytes,new[]{right},1);
            if(status!=0 && !(remove && LsaNtStatusToWinError(status)==2))throw new Win32Exception((int)LsaNtStatusToWinError(status));
        }finally{Marshal.FreeHGlobal(right.Buffer);LsaClose(policy);}
    }
    [DllImport("advapi32.dll")]static extern uint LsaOpenPolicy(IntPtr system,ref Attributes attributes,uint access,out IntPtr policy);
    [DllImport("advapi32.dll")]static extern uint LsaAddAccountRights(IntPtr policy,byte[] sid,UnicodeString[] rights,uint count);
    [DllImport("advapi32.dll")]static extern uint LsaRemoveAccountRights(IntPtr policy,byte[] sid,[MarshalAs(UnmanagedType.Bool)]bool all,UnicodeString[] rights,uint count);
    [DllImport("advapi32.dll")]static extern uint LsaClose(IntPtr policy);
    [DllImport("advapi32.dll")]static extern uint LsaNtStatusToWinError(uint status);
}
'@
    [UncFixtureBatchLogon]::Change($user.SID.Value,$false)
    $batchRightGranted = $true
    & icacls.exe $lab /grant ("*" + $user.SID.Value + ":(OI)(CI)M") /T /Q
    if ($LASTEXITCODE -ne 0) { throw 'Could not grant access to the owned fixture directory.' }
    @{WindowsSid=$user.SID.Value;PreviousDestination=$unc} | ConvertTo-Json |
        Set-Content (Join-Path $lab 'unc-fixture.json') -Encoding utf8
    $probeDirectory = Join-Path $lab 'probe'
    dotnet build tests/WindowsInstalledRecoveryProbe -c Release "-p:InstalledAgentDirectory=$Installation" -o $probeDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Could not build the installed-assembly UNC probe.' }
    $exe = Join-Path $probeDirectory 'WindowsInstalledRecoveryProbe.exe'

    $stage = 'create-copy-under-standard-user'
    New-SmbShare -Name $share -Path $backing -Description $marker -FullAccess $account -CachingMode None | Out-Null
    Invoke-Probe 'prepare-unc'
    Remove-OwnedShare
    $stage = 'restore-denied'
    New-SmbShare -Name $share -Path $backing -Description $marker -ReadAccess $runner -NoAccess $account -CachingMode None | Out-Null
    Invoke-Probe 'deny-unc'
    Remove-OwnedShare
    $stage = 'restore-with-share-read-only'
    New-SmbShare -Name $share -Path $backing -Description $marker -ReadAccess $account -CachingMode None | Out-Null
    Invoke-Probe 'restore-unc'
    $result = Get-Content (Join-Path $report 'historical-unc-result.json') -Raw | ConvertFrom-Json -AsHashtable
    if (!$result.success -or !$result.readOnlyShare -or !$result.standardWindowsUser) { throw 'Read-only recovery evidence is incomplete.' }
    $result.deniedShareLeftNoOutput = $true
    $result | ConvertTo-Json | Set-Content (Join-Path $Output 'historical-unc-result.json')
}
catch {
    @{success=$false;stage=$stage} | ConvertTo-Json | Set-Content (Join-Path $Output 'historical-unc-result.json')
    throw
}
finally {
    Remove-OwnedShare
    if ($batchRightGranted) { [UncFixtureBatchLogon]::Change($user.SID.Value,$true) }
    if ($user) { Remove-LocalUser -SID $user.SID }
    Remove-Item -LiteralPath $lab -Recurse -Force
}
