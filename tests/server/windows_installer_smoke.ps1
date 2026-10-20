param([Parameter(Mandatory=$true)][string]$Installer)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
$root=Join-Path $env:ProgramData 'OneCBackupServer'
$install=Join-Path $env:ProgramFiles '1C-Backup Server'
if (Test-Path $root) { throw 'Installer smoke requires a clean disposable machine.' }
$secret=Join-Path $env:TEMP ('onec-server-smoke-'+[Guid]::NewGuid().ToString('N')+'.txt')
$password=[Guid]::NewGuid().ToString('N')+[Guid]::NewGuid().ToString('N')
[IO.File]::WriteAllText($secret,$password,(New-Object Text.UTF8Encoding($false)))
New-Item -ItemType Directory -Path 'artifacts/server-tests' -Force | Out-Null
function Install-Server {
    $log=Join-Path (Get-Location).Path ('artifacts/server-tests/windows-installer-'+[Guid]::NewGuid().ToString('N')+'.log')
    $process=Start-Process -FilePath $Installer -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+$log+'"'),('/ADMINPASSWORDFILE="'+$secret+'"')) -PassThru
    if (!$process.WaitForExit(240000)) {
        & taskkill.exe /PID $process.Id /T /F | Out-Null
        throw 'Installer exceeded the four-minute phase timeout.'
    }
    if ($process.ExitCode -ne 0) { throw ('Installer failed: '+$process.ExitCode) }
}
function Health {
    $response=Invoke-WebRequest -UseBasicParsing 'https://localhost:7443/health/ready' -TimeoutSec 10
    if ($response.StatusCode -ne 200) { throw 'HTTPS readiness failed.' }
}
function Login {
    $session=New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $page=Invoke-WebRequest -UseBasicParsing 'https://localhost:7443/login' -WebSession $session
    $token=[Net.WebUtility]::HtmlDecode([regex]::Match($page.Content,'name="__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value)
    # Invoke-WebRequest follows the successful relative redirect and returns the authenticated panel.
    $panel=Invoke-WebRequest -UseBasicParsing 'https://localhost:7443/session/login' -Method Post -WebSession $session -Body @{name='admin';password=$password;__RequestVerificationToken=$token}
    if ($panel.BaseResponse.ResponseUri.AbsolutePath -eq '/login') { throw 'Administrator login failed.' }
    $management=Invoke-WebRequest -UseBasicParsing 'https://localhost:7443/api/v1/management/session' -WebSession $session
    if (($management.Content | ConvertFrom-Json).name -ne 'admin') { throw 'Administrator session not authenticated.' }
}
try {
    Install-Server
    Health
    Login
    $connectionHash=(Get-FileHash (Join-Path $root 'connection-string.txt')).Hash
    $certificateHash=(Get-FileHash (Join-Path $root 'server.pfx')).Hash
    if (Test-Path (Join-Path $root 'administrator-password.txt')) { throw 'Bootstrap password was not removed.' }
    foreach ($name in @('OneCBackupServer','OneCBackupServerDatabase')) {
        if ((Get-Service $name).Status -ne 'Running') { throw ('Service is not running: '+$name) }
        $service=Get-CimInstance Win32_Service -Filter "Name='$name'"
        if ($service.StartName -notin @('NT AUTHORITY\NetworkService','NT AUTHORITY\Network Service')) { throw 'Service must run without administrator privileges.' }
    }
    $acl=Get-Acl $root
    if ($acl.Access | Where-Object { $_.IdentityReference.Value -in @('Everyone','BUILTIN\Users','NT AUTHORITY\Authenticated Users') }) { throw 'Secrets ACL is too broad.' }
    Install-Server
    Health
    Login
    if ($connectionHash -ne (Get-FileHash (Join-Path $root 'connection-string.txt')).Hash -or $certificateHash -ne (Get-FileHash (Join-Path $root 'server.pfx')).Hash) { throw 'Reinstall changed persistent credentials/certificate.' }
    # Reboot-equivalent SCM stop/start; both services must recover with their installed identity.
    & (Join-Path $install 'windows\Stop-Server.ps1')
    Start-Service OneCBackupServerDatabase
    Start-Service OneCBackupServer
    Start-Sleep -Seconds 5
    Health
    Login
    $uninstall=Start-Process -FilePath (Join-Path $install 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) { throw 'Uninstall failed.' }
    if (Get-Service OneCBackupServer -ErrorAction SilentlyContinue) { throw 'Server service survived uninstall.' }
    if (!(Test-Path (Join-Path $root 'postgresql\PG_VERSION'))) { throw 'Uninstall deleted database.' }
    Install-Server
    Health
    Login
    New-Item -ItemType Directory -Path 'artifacts/server-tests' -Force | Out-Null
    @{ installed=$true; https=$true; administratorLogin=$true; reinstallPreservesData=$true; serviceRestart=$true; uninstallPreservesDatabase=$true; reinstallAfterUninstall=$true } | ConvertTo-Json | Set-Content 'artifacts/server-tests/windows-installer-result.json'
} catch {
    if (Test-Path (Join-Path $root 'setup.log')) { Get-Content (Join-Path $root 'setup.log') }
    Get-Service OneCBackupServer,OneCBackupServerDatabase -ErrorAction SilentlyContinue | Select-Object Name,Status | Format-Table
    throw
} finally {
    Remove-Item -LiteralPath $secret -ErrorAction SilentlyContinue
}
