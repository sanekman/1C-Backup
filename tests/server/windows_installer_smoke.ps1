param([Parameter(Mandatory=$true)][string]$Installer)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
$root=Join-Path $env:ProgramData 'OneCBackupServer'
$install=Join-Path $env:ProgramFiles '1C-Backup Server'
if (Test-Path $root) { throw 'Installer smoke requires a clean disposable machine.' }
$secret=Join-Path $env:TEMP ('onec-server-smoke-'+[Guid]::NewGuid().ToString('N')+'.txt')
$displayName='Бухгалтерия — основной сервер'
$address='https://127.0.0.1:7447/monitoring/'
$password=[Guid]::NewGuid().ToString('N')+[Guid]::NewGuid().ToString('N')
[IO.File]::WriteAllText($secret,$password,(New-Object Text.UTF8Encoding($false)))
New-Item -ItemType Directory -Path 'artifacts/server-tests' -Force | Out-Null
function Install-Server([string]$Name=$displayName,[string]$Url=$address) {
    $log=Join-Path (Get-Location).Path ('artifacts/server-tests/windows-installer-'+[Guid]::NewGuid().ToString('N')+'.log')
    $process=Start-Process -FilePath $Installer -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+$log+'"'),('/ADMINPASSWORDFILE="'+$secret+'"'),('/SERVERNAME="'+$Name+'"'),('/SERVERADDRESS="'+$Url+'"')) -PassThru
    if (!$process.WaitForExit(240000)) {
        & taskkill.exe /PID $process.Id /T /F | Out-Null
        throw 'Installer exceeded the four-minute phase timeout.'
    }
    if ($process.ExitCode -ne 0) { throw ('Installer failed: '+$process.ExitCode) }
}
function Health {
    $response=Invoke-WebRequest -UseBasicParsing ($address+'health/ready') -TimeoutSec 10
    if ($response.StatusCode -ne 200) { throw 'HTTPS readiness failed.' }
}
function Login {
    $session=New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $page=Invoke-WebRequest -UseBasicParsing ($address+'login') -WebSession $session
    if (![Net.WebUtility]::HtmlDecode($page.Content).Contains($displayName)) { throw 'Friendly server name missing from login page.' }
    $token=[Net.WebUtility]::HtmlDecode([regex]::Match($page.Content,'name="__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value)
    # Invoke-WebRequest follows the successful relative redirect and returns the authenticated panel.
    $panel=Invoke-WebRequest -UseBasicParsing ($address+'session/login') -Method Post -WebSession $session -Body @{name='admin';password=$password;__RequestVerificationToken=$token}
    $management=Invoke-WebRequest -UseBasicParsing ($address+'api/v1/management/session') -WebSession $session
    if (($management.Content | ConvertFrom-Json).name -ne 'admin') { throw 'Administrator session not authenticated.' }
}
try {
    Install-Server
    Health
    Login
    $settings=Get-Content (Join-Path $root 'server.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($settings.Server.DisplayName -ne $displayName -or $settings.Server.PublicAddress -ne $address) { throw 'Name and address were not stored independently.' }
    if ([IO.File]::ReadAllText((Join-Path $root 'panel-url.txt')) -ne $address) { throw 'Panel shortcut address differs.' }
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
    Install-Server 'Another monitoring system' 'https://different.example:8443/'
    Health
    Login
    $settings=Get-Content (Join-Path $root 'server.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($settings.Server.DisplayName -ne $displayName -or $settings.Server.PublicAddress -ne $address) { throw 'Reinstall overwrote the server identity.' }
    if ($connectionHash -ne (Get-FileHash (Join-Path $root 'connection-string.txt')).Hash -or $certificateHash -ne (Get-FileHash (Join-Path $root 'server.pfx')).Hash) { throw 'Reinstall changed persistent credentials/certificate.' }
    # Upgrade a legacy configuration with only panel-url.txt; do not regenerate its certificate or database.
    $settings.Server.PSObject.Properties.Remove('DisplayName')
    $settings.Server.PSObject.Properties.Remove('PublicAddress')
    [IO.File]::WriteAllText((Join-Path $root 'server.json'),($settings | ConvertTo-Json -Depth 5),(New-Object Text.UTF8Encoding($false)))
    Install-Server
    Health
    Login
    $settings=Get-Content (Join-Path $root 'server.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($settings.Server.DisplayName -ne $displayName -or $settings.Server.PublicAddress -ne $address) { throw 'Legacy identity migration failed.' }
    if ($connectionHash -ne (Get-FileHash (Join-Path $root 'connection-string.txt')).Hash -or $certificateHash -ne (Get-FileHash (Join-Path $root 'server.pfx')).Hash) { throw 'Migration changed persistent credentials/certificate.' }
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
    @{ separateNameAndAddress=$true; customHttpsPortAndPath=$true; ipCertificateValidated=$true; legacyIdentityMigration=$true; installed=$true; https=$true; administratorLogin=$true; reinstallPreservesData=$true; serviceRestart=$true; uninstallPreservesDatabase=$true; reinstallAfterUninstall=$true } | ConvertTo-Json | Set-Content 'artifacts/server-tests/windows-installer-result.json'
} catch {
    if (Test-Path (Join-Path $root 'setup.log')) { Get-Content (Join-Path $root 'setup.log') }
    Get-Service OneCBackupServer,OneCBackupServerDatabase -ErrorAction SilentlyContinue | Select-Object Name,Status | Format-Table
    throw
} finally {
    Remove-Item -LiteralPath $secret -ErrorAction SilentlyContinue
}
