$ErrorActionPreference='Stop'
# A parent PowerShell 7 process may pass incompatible module paths to Windows PowerShell 5.1.
if ($PSVersionTable.PSEdition -eq 'Desktop') { $env:PSModulePath=Join-Path $PSHOME 'Modules' }
foreach ($name in @('OneCBackupServer','OneCBackupServerDatabase')) {
    $service=Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
        Stop-Service -Name $name -Force
        $service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(60))
    }
}
