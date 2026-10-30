# Called by windows_lab.ps1. Both sessions belong to its two proven, isolated VMs.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][System.Management.Automation.Runspaces.PSSession]$AgentSession,
    [Parameter(Mandatory)][System.Management.Automation.Runspaces.PSSession]$ServerSession,
    [Parameter(Mandatory)][string]$Marker
)
$ErrorActionPreference = 'Stop'
if ($Marker -notmatch '^[a-f0-9]{32}$') { throw 'Invalid owned lab marker.' }
$serverControl = {
    param($marker, $action, $request)
    $ErrorActionPreference = 'Stop'
    $name = "SPBackupAllowed_$marker"
    $backing = 'C:\SPBackupShares\allowed'
    $share = Get-SmbShare -Name $name -ErrorAction SilentlyContinue
    if (Get-Content -LiteralPath (Join-Path $backing 'fixture-owner.txt') -Raw) {
        if ((Get-Content -LiteralPath (Join-Path $backing 'fixture-owner.txt') -Raw) -ne $marker) { throw 'SMB root ownership changed.' }
    } else { throw 'SMB ownership marker is absent.' }
    if ($share -and ($share.Path -ine $backing -or $share.Description -ne $marker)) { throw 'SMB share ownership changed.' }
    if ($action -eq 'disconnect') {
        if (-not $share -or $request.relativeDirectory -notmatch '^[a-f0-9]{32}$' -or
            $request.temporary -ne [IO.Path]::GetFileName($request.temporary) -or $request.temporary -notlike '*.tmp') {
            throw 'Refusing an unowned SMB interruption request.'
        }
        $file = Join-Path (Join-Path $backing $request.relativeDirectory) $request.temporary
        $handles = @(Get-SmbOpenFile | Where-Object { $_.Path -ieq $file })
        if ($handles.Count -ne 1 -or $handles[0].ClientUserName -ine 'SPBLAB\Administrator' -or
            (Get-Item -LiteralPath $file).Length -le 0) { throw 'No matching real remote SMB handle and flushed first block.' }
        Remove-SmbShare -Name $name -Force -Confirm:$false
        if (Get-SmbShare -Name $name -ErrorAction SilentlyContinue) { throw 'The remote share did not disconnect.' }
    } elseif ($action -eq 'restore' -or $action -eq 'cleanup') {
        if (-not $share) {
            New-SmbShare -Name $name -Path $backing -Description $marker -FullAccess 'SPBLAB\Administrator','SPBLAB\spbackup-service' -CachingMode None | Out-Null
        } elseif ($action -eq 'restore') { throw 'The remote share was restored before the actual production retry.' }
    } else { throw 'Unknown remote SMB controller action.' }
}
try {
    Invoke-Command -Session $AgentSession -ArgumentList $Marker -ScriptBlock {
        param($marker)
        $env:ONECBACKUP_NATIVE_SMB = '1'; $env:SPBACKUP_REMOTE_SMB_TEST = '1'
        $env:SPBACKUP_REMOTE_SMB_SETTINGS = 'C:\SPBackupLab\remote-smb-settings.json'
        if (Test-Path 'C:\SPBackupLab\remote-smb-results') { throw 'Use a fresh lab instead of replacing SMB evidence.' }
        New-Item -ItemType Directory 'C:\SPBackupLab\remote-smb-results' | Out-Null
        @{ UncRoot = "\\SPB-DC\SPBackupAllowed_$marker"; Marker = $marker; ControllerDirectory = 'C:\SPBackupLab\smb-controller' } |
            ConvertTo-Json | Set-Content -LiteralPath $env:SPBACKUP_REMOTE_SMB_SETTINGS -Encoding UTF8
        $global:spBackupSmbTest = Start-Process 'C:\SPBackupLab\dotnet\dotnet.exe' -WorkingDirectory 'C:\SPBackupLab' `
            -ArgumentList @('vstest', 'C:\SPBackupLab\tests\SPBackup.Core.Tests.dll', '/TestCaseFilter:Category=NativeSmbIntegration',
                '/Logger:trx;LogFileName=remote-smb.trx', '/ResultsDirectory:C:\SPBackupLab\remote-smb-results') `
            -RedirectStandardOutput 'C:\SPBackupLab\remote-smb-results\runner.log' `
            -RedirectStandardError 'C:\SPBackupLab\remote-smb-results\runner-error.log' -PassThru
    }
    $handled = @{}
    $deadline = [DateTime]::UtcNow.AddMinutes(4)
    do {
        foreach ($action in @('disconnect', 'restore')) {
            if ($handled.ContainsKey($action)) { continue }
            $request = Invoke-Command -Session $AgentSession -ArgumentList $action -ScriptBlock {
                param($action)
                $path = "C:\SPBackupLab\smb-controller\$action-request.json"
                if (Test-Path $path) { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
            }
            if (-not $request) { continue }
            Invoke-Command -Session $ServerSession -ScriptBlock $serverControl -ArgumentList $Marker, $action, $request
            Invoke-Command -Session $AgentSession -ArgumentList $action, $request.requestId -ScriptBlock {
                param($action, $id)
                $path = "C:\SPBackupLab\smb-controller\$action-response.json"
                @{ requestId = $id; succeeded = $true } | ConvertTo-Json | Set-Content -LiteralPath ($path + '.tmp') -Encoding UTF8
                Move-Item -LiteralPath ($path + '.tmp') -Destination $path
            }
            $handled[$action] = $true
        }
        $result = Invoke-Command -Session $AgentSession -ScriptBlock {
            if ($global:spBackupSmbTest.HasExited) { @{ exited = $true; code = $global:spBackupSmbTest.ExitCode } }
            else { @{ exited = $false } }
        }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'The separate-server SMB test exceeded its deadline.' }
        if (-not $result.exited) { Start-Sleep -Milliseconds 100 }
    } until ($result.exited)
    if ($result.code -ne 0 -or $handled.Count -ne 2) { throw 'The real remote SMB interruption/retry test did not pass both barriers.' }
    Invoke-Command -Session $AgentSession -ScriptBlock {
        [xml]$trx = Get-Content 'C:\SPBackupLab\remote-smb-results\remote-smb.trx'
        $counts = $trx.TestRun.ResultSummary.Counters
        if ([int]$counts.total -ne 1 -or [int]$counts.passed -ne 1 -or [int]$counts.notExecuted -ne 0) {
            throw 'A skipped/missing native SMB test is not successful separate-server proof.'
        }
    }
} finally {
    Invoke-Command -Session $ServerSession -ScriptBlock $serverControl -ArgumentList $Marker, 'cleanup', $null
}
