$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'Stop-Server.ps1')
foreach ($name in @('OneCBackupServer','OneCBackupServerDatabase')) {
    if (Get-Service -Name $name -ErrorAction SilentlyContinue) {
        & sc.exe delete $name | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Unable to remove Windows service.' }
    }
}
Get-NetFirewallRule -Name 'OneCBackupServer-Https' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
# Deliberately retain ProgramData, PostgreSQL data, certificate trust and panel URL for reinstall/recovery.
