#requires -RunAsAdministrator
#requires -Modules Hyper-V
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Create', 'Test', 'Remove')][string]$Action,
    [Parameter(Mandatory)][string]$Root,
    [string]$BaseVhdx,
    [string]$Bundle,
    [PSCredential]$BaseCredential
)
$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath($Root)
$statePath = Join-Path $Root 'lab.json'
function Save-Lab {
    $temporary = $statePath + '.tmp'
    $script:lab | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $statePath -Force
}
function Open-Guest([string]$Name, [PSCredential]$Credential) {
    $deadline = [DateTime]::UtcNow.AddMinutes(10)
    do {
        try { $session = New-PSSession -VMName $Name -Credential $Credential -ErrorAction Stop }
        catch { if ([DateTime]::UtcNow -gt $deadline) { throw }; Start-Sleep -Seconds 3; continue }
        try {
            # The host's preference is not inherited by a remote runspace. Every subsequent
            # provisioning/verification command in this session must stop on a cmdlet error.
            $null = Invoke-Command -Session $session -ScriptBlock { $global:ErrorActionPreference = 'Stop' } -ErrorAction Stop
            return $session
        } catch { Remove-PSSession $session; throw }
    } while ($true)
}
function Assert-VM($Item) {
    $vm = Get-VM -Id $Item.Id -ErrorAction Stop
    if ($vm.Name -ne $Item.Name -or $vm.Notes -ne "Owned SP Backup lab $($lab.Id)") { throw 'The VM ownership marker changed.' }
    $networks = @(Get-VMNetworkAdapter -VM $vm)
    if ($networks.Count -ne 1 -or $networks[0].SwitchId -ne $lab.SwitchId) { throw 'The VM is no longer isolated on its own private switch.' }
    $disks = @(Get-VMHardDiskDrive -VM $vm)
    if ($disks.Count -ne 1 -or $disks[0].Path -ine (Join-Path $Root "$($Item.Role).vhdx") -or @(Get-VMSnapshot -VM $vm).Count -ne 0) {
        throw 'The VM disk ownership or snapshot layout changed.'
    }
    return $vm
}
function Assert-Switch {
    $network = Get-VMSwitch -Id $lab.SwitchId
    if ($network.Name -ne $lab.SwitchName -or $network.SwitchType -ne 'Private' -or $network.Notes -ne "Owned SP Backup lab $($lab.Id)") {
        throw 'The lab switch ownership/isolation changed.'
    }
}
function Restart-Guest($Item) {
    $vm = Assert-VM $Item
    # Role installation, promotion and domain join require an orderly guest shutdown.
    # The intentional abrupt power cycle exists only at the active-backup fault barrier.
    Stop-VM -VM $vm -ErrorAction Stop
    if ((Get-VM -Id $Item.Id -ErrorAction Stop).State -ne 'Off') { throw 'The guest did not shut down; refusing to reset pending Windows configuration.' }
    Start-VM -VM $vm -ErrorAction Stop | Out-Null
}
if ($Action -eq 'Create') {
    if (-not $BaseCredential -or -not (Test-Path -LiteralPath $BaseVhdx -PathType Leaf) -or -not (Test-Path -LiteralPath (Join-Path $Bundle 'bundle.json'))) {
        throw 'Provide a generalized Windows Server VHDX, its first-boot local administrator credential, and a prepared lab bundle.'
    }
    if (Test-Path -LiteralPath $Root) { throw 'Use a new lab directory. Existing VMs, profiles and lab results are never replaced.' }
    if ((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory -lt 8GB / 1KB) { throw 'At least 8 GiB of free host RAM is required.' }
    $BaseVhdx = (Resolve-Path -LiteralPath $BaseVhdx).Path
    $Bundle = (Resolve-Path -LiteralPath $Bundle).Path
    $manifest = Get-Content -LiteralPath (Join-Path $Bundle 'bundle.json') -Raw | ConvertFrom-Json
    if ($manifest.format -ne 1 -or $manifest.sdk -ne '10.0.402' -or $manifest.runtime -ne '10.0.13' -or -not $manifest.runnerVerified) { throw 'Unexpected or unverified lab bundle format/SDK/runtime.' }
    foreach ($file in $manifest.files) {
        $path = [IO.Path]::GetFullPath((Join-Path $Bundle $file.path))
        if (-not $path.StartsWith($Bundle + '\', [StringComparison]::OrdinalIgnoreCase) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw 'The bundle integrity check failed.' }
    }
    New-Item -ItemType Directory -Path $Root | Out-Null
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User.Value, 'S-1-5-18', 'S-1-5-32-544')) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
            (New-Object Security.Principal.SecurityIdentifier($sid)), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    Set-Acl -LiteralPath $Root -AclObject $acl
    $id = [Guid]::NewGuid().ToString('N')
    $script:lab = @{ Format = 1; Id = $id; OwnerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value;
        Revision = $manifest.revision; BaseVhdx = $BaseVhdx; BaseSha256 = (Get-FileHash -LiteralPath $BaseVhdx -Algorithm SHA256).Hash;
        SwitchName = "SPBackupLab-$($id.Substring(0,8))"; SwitchId = $null; VMs = @(); Ready = $false }
    Save-Lab
    $BaseCredential | Export-Clixml -LiteralPath (Join-Path $Root 'owner-credential.xml')
    $network = New-VMSwitch -Name $lab.SwitchName -SwitchType Private -Notes "Owned SP Backup lab $id"
    $lab.SwitchId = $network.Id.ToString(); Save-Lab
    foreach ($role in @('DC', 'Agent')) {
        $name = "SPBackup-$role-$($id.Substring(0,8))"
        $disk = Join-Path $Root "$role.vhdx"
        New-VHD -Path $disk -ParentPath $BaseVhdx -Differencing | Out-Null
        $vm = New-VM -Name $name -Generation 2 -MemoryStartupBytes 4GB -VHDPath $disk -Path $Root -SwitchName $lab.SwitchName
        Set-VM -VM $vm -ProcessorCount 2 -AutomaticStartAction Nothing -AutomaticStopAction ShutDown -AutomaticCheckpointsEnabled $false -Notes "Owned SP Backup lab $id"
        Set-VMMemory -VM $vm -DynamicMemoryEnabled $false
        $lab.VMs += @{ Name = $name; Id = $vm.Id.ToString(); Role = $role }; Save-Lab
        Start-VM -VM $vm | Out-Null
    }
    $dc = Open-Guest $lab.VMs[0].Name $BaseCredential
    $agent = Open-Guest $lab.VMs[1].Name $BaseCredential
    try {
        $machineSids = @($dc, $agent) | ForEach-Object { Invoke-Command -Session $_ -ScriptBlock {
            if ((Get-CimInstance Win32_ComputerSystem).PartOfDomain) { throw 'Use a workgroup base image, never a member of an existing domain.' }
            ([Security.Principal.WindowsIdentity]::GetCurrent().User.AccountDomainSid).Value
        } }
        if ($machineSids[0] -eq $machineSids[1]) { throw 'The base image was not generalized: guests have identical machine SIDs. Use a correctly prepared image.' }
        Invoke-Command -Session $dc -ScriptBlock {
            Rename-Computer -NewName SPB-DC -Force
            $nic = Get-NetAdapter | Where-Object Status -eq 'Up'
            if (@($nic).Count -ne 1) { throw 'Expected one isolated network adapter.' }
            Set-NetIPInterface -InterfaceIndex $nic.ifIndex -Dhcp Disabled
            New-NetIPAddress -InterfaceIndex $nic.ifIndex -IPAddress 192.168.253.10 -PrefixLength 24 | Out-Null
            Set-DnsClientServerAddress -InterfaceIndex $nic.ifIndex -ServerAddresses 192.168.253.10
            $role = Install-WindowsFeature AD-Domain-Services -IncludeManagementTools
            if (-not $role.Success) { throw 'AD DS role installation failed; the domain was not provisioned.' }
        }
        Invoke-Command -Session $agent -ScriptBlock {
            Rename-Computer -NewName SPB-AGENT -Force
            $nic = Get-NetAdapter | Where-Object Status -eq 'Up'
            if (@($nic).Count -ne 1) { throw 'Expected one isolated network adapter.' }
            Set-NetIPInterface -InterfaceIndex $nic.ifIndex -Dhcp Disabled
            New-NetIPAddress -InterfaceIndex $nic.ifIndex -IPAddress 192.168.253.20 -PrefixLength 24 | Out-Null
            Set-DnsClientServerAddress -InterfaceIndex $nic.ifIndex -ServerAddresses 192.168.253.10
        }
    } finally { Remove-PSSession $dc, $agent }
    foreach ($item in $lab.VMs) { Restart-Guest $item }
    $dc = Open-Guest $lab.VMs[0].Name $BaseCredential
    try {
        Invoke-Command -Session $dc -ArgumentList $BaseCredential.Password -ScriptBlock {
            param($password)
            Install-ADDSForest -DomainName spbackup.test -DomainNetbiosName SPBLAB -InstallDns `
                -SafeModeAdministratorPassword $password -NoRebootOnCompletion -Force | Out-Null
        }
    } finally { Remove-PSSession $dc }
    Restart-Guest $lab.VMs[0]
    $domain = New-Object Management.Automation.PSCredential('SPBLAB\Administrator', $BaseCredential.Password)
    $domain | Export-Clixml -LiteralPath (Join-Path $Root 'domain-credential.xml')
    $dc = Open-Guest $lab.VMs[0].Name $domain
    $agent = Open-Guest $lab.VMs[1].Name $BaseCredential
    try {
        Invoke-Command -Session $dc -ScriptBlock {
            $deadline = [DateTime]::UtcNow.AddMinutes(5)
            while (-not (Get-ADDomain -ErrorAction SilentlyContinue)) {
                if ([DateTime]::UtcNow -gt $deadline) { throw 'AD DS did not become ready.' }; Start-Sleep -Seconds 2
            }
        }
        Invoke-Command -Session $agent -ArgumentList $domain -ScriptBlock {
            param($credential)
            $deadline = [DateTime]::UtcNow.AddMinutes(5)
            do {
                try { $null = Resolve-DnsName -Type SRV '_ldap._tcp.dc._msdcs.spbackup.test' -ErrorAction Stop; break }
                catch { if ([DateTime]::UtcNow -gt $deadline) { throw }; Start-Sleep -Seconds 2 }
            } while ($true)
            Add-Computer -DomainName spbackup.test -Credential $credential -Force
        }
        Copy-Item -LiteralPath $Bundle -Destination 'C:\SPBackupLab' -ToSession $agent -Recurse
    } finally { Remove-PSSession $dc, $agent }
    Restart-Guest $lab.VMs[1]
    $bytes = New-Object byte[] 24
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $servicePassword = ConvertTo-SecureString ('SpB!9-' + [Convert]::ToBase64String($bytes)) -AsPlainText -Force
    $service = New-Object Management.Automation.PSCredential('SPBLAB\spbackup-service', $servicePassword)
    $service | Export-Clixml -LiteralPath (Join-Path $Root 'service-credential.xml')
    $dc = Open-Guest $lab.VMs[0].Name $domain
    try {
        $lab.DomainSid = Invoke-Command -Session $dc -ArgumentList $servicePassword, $id -ScriptBlock {
            param($password, $marker)
            New-ADUser -Name spbackup-service -SamAccountName spbackup-service -AccountPassword $password -Enabled $true -PasswordNeverExpires $true
            $directory = 'C:\SPBackupShares'
            New-Item -ItemType Directory -Path $directory | Out-Null
            foreach ($kind in @('allowed', 'denied')) {
                $path = Join-Path $directory $kind
                New-Item -ItemType Directory -Path $path | Out-Null
                Set-Content -LiteralPath (Join-Path $path 'fixture-owner.txt') -Value $marker -NoNewline
                $acl = Get-Acl -LiteralPath $path
                $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule('SPBLAB\spbackup-service', 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
                Set-Acl -LiteralPath $path -AclObject $acl
                if ($kind -eq 'allowed') {
                    New-SmbShare -Name "SPBackupAllowed_$marker" -Path $path -Description $marker -FullAccess 'SPBLAB\Administrator','SPBLAB\spbackup-service' -CachingMode None | Out-Null
                } else {
                    New-SmbShare -Name "SPBackupDenied_$marker" -Path $path -Description $marker -FullAccess 'SPBLAB\Administrator' -NoAccess 'SPBLAB\spbackup-service' -CachingMode None | Out-Null
                }
            }
            New-NetFirewallRule -DisplayName "SP Backup isolated lab SMB $marker" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 445 -RemoteAddress 192.168.253.20 | Out-Null
            (Get-ADDomain).DomainSID.Value
        }
    } finally { Remove-PSSession $dc }
    $lab.Ready = $true; Save-Lab
    Write-Output "Isolated lab created: $Root. Run -Action Test next. No guest is connected to an external switch."
    exit
}

$script:lab = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
if ($lab.Format -ne 1 -or $lab.OwnerSid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value) { throw 'Use the original lab owner.' }
Assert-Switch
foreach ($item in $lab.VMs) { $null = Assert-VM $item }
if ($Action -eq 'Remove') {
    foreach ($item in $lab.VMs) {
        $vm = Assert-VM $item
        if ($vm.State -ne 'Off') { Stop-VM -VM $vm -TurnOff -Force }
        Remove-VM -VM $vm -Force
    }
    Remove-VMSwitch -VMSwitch (Get-VMSwitch -Id $lab.SwitchId) -Force
    # Keep lab.json, results and encrypted host credentials for explicit inspection/deletion.
    # Differencing disks can be large; only exact owned disk paths are removed.
    foreach ($role in @('DC','Agent')) { Remove-Item -LiteralPath (Join-Path $Root "$role.vhdx") }
    Write-Output 'Owned VMs, switch and differencing disks removed. Source image and results retained.'
    exit
}
if (-not $lab.Ready) { throw 'Provisioning did not finish. Inspect the stand; tests are not marked successful.' }
$domain = Import-Clixml -LiteralPath (Join-Path $Root 'domain-credential.xml')
$service = Import-Clixml -LiteralPath (Join-Path $Root 'service-credential.xml')
$dc = Open-Guest $lab.VMs[0].Name $domain
$agent = Open-Guest $lab.VMs[1].Name $domain
$results = Join-Path $Root ('results-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $results | Out-Null
try {
    Invoke-Command -Session $agent -ArgumentList $lab.Revision -ScriptBlock {
        param($revision)
        $bundleRoot = 'C:\SPBackupLab'
        $manifest = Get-Content -LiteralPath (Join-Path $bundleRoot 'bundle.json') -Raw | ConvertFrom-Json
        if ($manifest.revision -ne $revision) { throw 'The guest has a different source bundle.' }
        foreach ($file in $manifest.files) {
            $path = [IO.Path]::GetFullPath((Join-Path $bundleRoot $file.path))
            if (-not $path.StartsWith($bundleRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
                (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw 'Guest bundle integrity failed.' }
        }
    }
    # The domain fixture password travels only in encrypted PowerShell Direct and child stdin.
    Invoke-Command -Session $agent -ArgumentList $service, $lab.DomainSid, $lab.Id, $lab.Revision -ScriptBlock {
        param($service, $domainSid, $id, $revision)
        $env:ONECBACKUP_NATIVE_SERVICE_TEST = '1'; $env:SPBACKUP_DOMAIN_SMB_TEST = '1'
        $env:PGBIN = 'C:\SPBackupLab\postgresql\bin'; $env:GITHUB_SHA = $revision
        $settings = @{ Account = $service.UserName; Password = $service.GetNetworkCredential().Password;
            AllowedUnc = "\\SPB-DC\SPBackupAllowed_$id"; DeniedUnc = "\\SPB-DC\SPBackupDenied_$id"; DomainSid = $domainSid } | ConvertTo-Json
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = 'C:\SPBackupLab\probe\WindowsAgentServiceSmoke.exe'
        $start.Arguments = 'C:\SPBackupLab\agent\SPBackup.Agent.exe C:\SPBackupLab\domain-result --domain-smb'
        $start.UseShellExecute = $false; $start.RedirectStandardInput = $true
        $process = [Diagnostics.Process]::Start($start)
        $process.StandardInput.Write($settings); $process.StandardInput.Close(); $settings = $null
        if (-not $process.WaitForExit(600000)) { throw 'Domain service fixture exceeded its deadline; inspect the guest.' }
        if ($process.ExitCode -ne 0) { throw 'Actual domain service backup/restore verification failed.' }
    }
    Copy-Item -Path 'C:\SPBackupLab\domain-result\service-result.json' -Destination (Join-Path $results 'domain-service-result.json') -FromSession $agent
    & (Join-Path $PSScriptRoot 'windows_lab_smb.ps1') -AgentSession $agent -ServerSession $dc -Marker $lab.Id
    Copy-Item -Path 'C:\SPBackupLab\remote-smb-results\*' -Destination $results -FromSession $agent -Recurse
    # Start preparation in a persistent PS Direct session; retain it until the VM is powered off.
    Invoke-Command -Session $agent -ArgumentList $lab.Revision -ScriptBlock {
        param($revision)
        $env:ONECBACKUP_NATIVE_SERVICE_TEST = '1'; $env:PGBIN = 'C:\SPBackupLab\postgresql\bin'; $env:GITHUB_SHA = $revision
        if (Test-Path 'C:\SPBackupLab\reboot-result') { throw 'Reboot results already exist; create a new lab rather than replacing evidence.' }
        New-Item -ItemType Directory 'C:\SPBackupLab\reboot-result' | Out-Null
        $global:spBackupRebootPreparation = Start-Process 'C:\SPBackupLab\probe\WindowsAgentServiceSmoke.exe' -ArgumentList @(
            'C:\SPBackupLab\agent\SPBackup.Agent.exe', 'C:\SPBackupLab\reboot-result', '--prepare-reboot') `
            -RedirectStandardOutput 'C:\SPBackupLab\reboot-result\prepare.log' -RedirectStandardError 'C:\SPBackupLab\reboot-result\prepare-error.log' -PassThru
    }
    $deadline = [DateTime]::UtcNow.AddMinutes(8)
    do {
        $ready = Invoke-Command -Session $agent -ScriptBlock {
            if ($global:spBackupRebootPreparation.HasExited) {
                throw "Reboot preparation exited before the fault barrier (code $($global:spBackupRebootPreparation.ExitCode)); inspect prepare.log and service-result.json."
            }
            $path = 'C:\SPBackupLab\reboot-result\reboot-ready.json'
            if (Test-Path $path) { try { Get-Content $path -Raw | ConvertFrom-Json } catch { } }
        }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'The real pg_dump did not reach the reboot barrier.' }
        if (-not $ready) { Start-Sleep -Milliseconds 500 }
    } until ($ready)
    if ([DateTimeOffset]::Parse($ready.expiresUtc).UtcDateTime -lt [DateTime]::UtcNow.AddSeconds(5) -or $ready.revision -ne $lab.Revision) {
        throw 'The reboot barrier is expired or belongs to another source revision.'
    }
    # Actual abrupt VM power cycle. No service/process kill can satisfy the boot-ID assertion.
    Stop-VM -VM (Assert-VM $lab.VMs[1]) -TurnOff -Force
    Remove-PSSession $agent; $agent = $null
    Start-VM -VM (Assert-VM $lab.VMs[1]) | Out-Null
    $agent = Open-Guest $lab.VMs[1].Name $domain
    Invoke-Command -Session $agent -ScriptBlock {
        $env:ONECBACKUP_NATIVE_SERVICE_TEST = '1'; $env:PGBIN = 'C:\SPBackupLab\postgresql\bin'
        & 'C:\SPBackupLab\probe\WindowsAgentServiceSmoke.exe' 'C:\SPBackupLab\agent\SPBackup.Agent.exe' 'C:\SPBackupLab\reboot-result' --verify-reboot
        if ($LASTEXITCODE -ne 0) { throw 'Real OS boot recovery verification failed.' }
    }
    Copy-Item -Path 'C:\SPBackupLab\reboot-result\reboot-result.json' -Destination $results -FromSession $agent
    Write-Output "All stand phases passed for $($lab.Revision). Evidence: $results"
} finally {
    if ($agent) {
        foreach ($path in @('domain-result\service-result.json', 'domain-result\failure-context.json', 'remote-smb-results',
            'reboot-result\reboot-result.json', 'reboot-result\service-result.json', 'reboot-result\failure-context.json',
            'reboot-result\prepare.log', 'reboot-result\prepare-error.log')) {
            try { Copy-Item -LiteralPath (Join-Path 'C:\SPBackupLab' $path) -Destination $results -FromSession $agent -Recurse -Force -ErrorAction Stop }
            catch { Write-Verbose "Evidence unavailable: $path" }
        }
    }
    if ($agent) { Remove-PSSession $agent }
    Remove-PSSession $dc
}
