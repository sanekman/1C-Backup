# Unit tests of the real controller functions with in-memory cmdlet substitutes.
# No Hyper-V module is imported, no VM/network/disk is created, stopped or removed.
$ErrorActionPreference = 'Stop'
$originalGlobalPreference = $global:ErrorActionPreference
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$errors = $null; $tokens = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'tools/windows_lab.ps1'), [ref]$tokens, [ref]$errors)
if ($errors) { throw ($errors | Out-String) }
foreach ($name in @('Open-Guest', 'Assert-VM', 'Restart-Guest')) {
    $definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (-not $definition) { throw "Controller function absent: $name" }
    . ([ScriptBlock]::Create($definition.Extent.Text))
}
function Reset-Fixture {
    $script:Root = Join-Path ([IO.Path]::GetTempPath()) 'SPBackup-controller-unit-only'
    $script:lab = @{ Id = 'unit'; SwitchId = 'private-unit-switch' }
    $script:item = @{ Id = 'unit-vm-id'; Name = 'unit-agent'; Role = 'Agent' }
    $script:vm = [pscustomobject]@{ Id = $item.Id; Name = $item.Name; Notes = 'Owned SP Backup lab unit'; State = 'Running' }
    $script:network = [pscustomobject]@{ SwitchId = $lab.SwitchId }
    $script:disk = [pscustomobject]@{ Path = (Join-Path $Root 'Agent.vhdx') }
    $script:snapshots = @(); $script:calls = @(); $script:removed = @()
    $script:stopFails = $false; $script:stopCompletes = $true; $script:initFails = $false
    $script:connectFailures = 0; $script:connects = 0; $script:sleeps = 0
}
function Get-VM { [CmdletBinding()]param($Id) return $script:vm }
function Get-VMNetworkAdapter { param($VM) return $script:network }
function Get-VMHardDiskDrive { param($VM) return $script:disk }
function Get-VMSnapshot { param($VM) return $script:snapshots }
function Stop-VM {
    [CmdletBinding()]param($VM, [switch]$TurnOff, [switch]$Force)
    $script:calls += @{ action = 'stop'; turnOff = [bool]$TurnOff; force = [bool]$Force }
    if ($script:stopFails) { throw 'unit shutdown failure' }
    if ($script:stopCompletes) { $script:vm.State = 'Off' }
}
function Start-VM {
    [CmdletBinding()]param($VM)
    $script:calls += @{ action = 'start' }; $script:vm.State = 'Running'
}
function New-PSSession {
    [CmdletBinding()]param($VMName, $Credential)
    $script:connects++
    if ($script:connectFailures -gt 0) { $script:connectFailures--; throw 'unit transport unavailable' }
    return [pscustomobject]@{ Name = $VMName; UnitOnly = $true }
}
function Invoke-Command {
    [CmdletBinding()]param($Session, [ScriptBlock]$ScriptBlock)
    if ($script:initFails) { throw 'unit remote initialization failed' }
    & $ScriptBlock
}
function Remove-PSSession { param($Session) $script:removed += $Session }
function Start-Sleep { param($Seconds) $script:sleeps++ }
function Assert-True($Value, [string]$Message) { if (-not $Value) { throw $Message } }
function Assert-Throws([ScriptBlock]$Run, [string]$Message) {
    $caught = $null
    try { & $Run } catch { $caught = $_ }
    Assert-True ($null -ne $caught -and $caught.Exception.Message.Contains($Message)) "Expected failure: $Message"
}
$script:passed = @()
function Case([string]$Name, [ScriptBlock]$Run) {
    Reset-Fixture
    & $Run
    $script:passed += $Name
}
$credential = New-Object Management.Automation.PSCredential('unit-only', (ConvertTo-SecureString 'not-a-real-password' -AsPlainText -Force))
try {
    Case 'remote-session-stops-on-cmdlet-errors' {
        $global:ErrorActionPreference = 'Continue'
        $session = Open-Guest 'unit-agent' $credential
        Assert-True ($session.UnitOnly -and $global:ErrorActionPreference -eq 'Stop') 'The remote preference was not initialized.'
    }
    Case 'failed-session-initialization-closes-session-without-retrying' {
        $script:initFails = $true
        Assert-Throws { Open-Guest 'unit-agent' $credential } 'unit remote initialization failed'
        Assert-True ($script:connects -eq 1 -and $script:removed.Count -eq 1) 'Initialization failure leaked/retried a session.'
    }
    Case 'transport-startup-failure-retries-before-initialization' {
        $script:connectFailures = 1
        $session = Open-Guest 'unit-agent' $credential
        Assert-True ($session.UnitOnly -and $script:connects -eq 2 -and $script:sleeps -eq 1) 'Transport retry failed.'
    }
    Case 'provisioning-shutdown-is-orderly-before-start' {
        Restart-Guest $item
        Assert-True ($script:calls.Count -eq 2 -and $script:calls[0].action -eq 'stop' -and $script:calls[1].action -eq 'start') 'Incorrect restart order.'
        Assert-True (-not $script:calls[0].turnOff -and -not $script:calls[0].force) 'Provisioning used a forced power cycle.'
    }
    Case 'shutdown-error-prevents-start' {
        $script:stopFails = $true
        Assert-Throws { Restart-Guest $item } 'unit shutdown failure'
        Assert-True ($script:calls.Count -eq 1) 'Started a guest after failed shutdown.'
    }
    Case 'incomplete-shutdown-prevents-start' {
        $script:stopCompletes = $false
        Assert-Throws { Restart-Guest $item } 'did not shut down'
        Assert-True ($script:calls.Count -eq 1) 'Reset a guest still applying Windows configuration.'
    }
    Case 'foreign-vm-is-never-stopped' {
        $script:vm.Notes = 'not this lab'
        Assert-Throws { Restart-Guest $item } 'ownership marker changed'
        Assert-True ($script:calls.Count -eq 0) 'Stopped a foreign VM.'
    }
    Case 'external-network-is-never-stopped' {
        $script:network.SwitchId = 'foreign-network'
        Assert-Throws { Restart-Guest $item } 'no longer isolated'
        Assert-True ($script:calls.Count -eq 0) 'Stopped a VM outside the private lab.'
    }
    Case 'foreign-disk-is-never-stopped' {
        $script:disk.Path = Join-Path $Root 'foreign.vhdx'
        Assert-Throws { Restart-Guest $item } 'disk ownership'
        Assert-True ($script:calls.Count -eq 0) 'Stopped a VM using a foreign disk.'
    }
    Case 'unexpected-snapshot-is-never-stopped' {
        $script:snapshots = @('unexpected checkpoint')
        Assert-Throws { Restart-Guest $item } 'snapshot layout changed'
        Assert-True ($script:calls.Count -eq 0) 'Stopped a VM with an unrecognized disk chain.'
    }
    @{ success = $true; scope = 'controller-unit-only'; actualHyperV = $false; checks = $script:passed } | ConvertTo-Json -Depth 4 -Compress
} finally { $global:ErrorActionPreference = $originalGlobalPreference }
