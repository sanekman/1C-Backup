$ErrorActionPreference='Stop'
$maintenanceCreated=$false
$maintenance=[Threading.Mutex]::new($false,'Global\SPBackupServerConfiguration',[ref]$maintenanceCreated)
$maintenanceHeld=$false
try {
    if (!$maintenanceCreated) { throw 'Server backup, restore or configuration is already running.' }
    try { $maintenanceHeld=$maintenance.WaitOne(0) } catch [Threading.AbandonedMutexException] { $maintenance.ReleaseMutex(); throw 'Previous server maintenance ended unexpectedly.' }
    if (!$maintenanceHeld) { throw 'Server backup, restore or configuration is already running.' }
$ErrorActionPreference='Stop'
# A parent PowerShell 7 process may pass incompatible module paths to Windows PowerShell 5.1.
if ($PSVersionTable.PSEdition -eq 'Desktop') { $env:PSModulePath=Join-Path $PSHOME 'Modules' }
& (Join-Path $PSScriptRoot 'Stop-Server.ps1')
foreach ($name in @('OneCBackupServer','OneCBackupServerDatabase')) {
    if (Get-Service -Name $name -ErrorAction SilentlyContinue) {
        & sc.exe delete $name | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Unable to remove Windows service.' }
    }
}
Get-NetFirewallRule -Name 'OneCBackupServer-Https' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
# Deliberately retain ProgramData, PostgreSQL data, certificate trust and panel URL for reinstall/recovery.

} finally { if ($maintenanceHeld) { $maintenance.ReleaseMutex() }; $maintenance.Dispose() }
