$ErrorActionPreference='Stop'
foreach ($name in @('OneCBackupServer','OneCBackupServerDatabase')) {
    $service=Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
        Stop-Service -Name $name -Force
        $service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(60))
    }
}
