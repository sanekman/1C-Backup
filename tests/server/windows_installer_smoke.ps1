param([Parameter(Mandatory=$true)][string]$Installer)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
$root=Join-Path $env:ProgramData 'OneCBackupServer'
$install=Join-Path $env:ProgramFiles 'SP Backup Server'
if (Test-Path $root) { throw 'Installer smoke requires a clean disposable machine.' }
$secret=Join-Path $env:TEMP ('onec-server-smoke-'+[Guid]::NewGuid().ToString('N')+'.txt')
$displayName='Бухгалтерия — основной сервер'
$address='https://127.0.0.1:7447/monitoring/'
$password=[Guid]::NewGuid().ToString('N')+[Guid]::NewGuid().ToString('N')
[IO.File]::WriteAllText($secret,$password,(New-Object Text.UTF8Encoding($false)))
New-Item -ItemType Directory -Path 'artifacts/server-tests' -Force | Out-Null
function Install-Server([string]$Name=$displayName,[string]$Url=$address,[string]$Port='',[string]$Firewall='') {
    $log=Join-Path (Get-Location).Path ('artifacts/server-tests/windows-installer-'+[Guid]::NewGuid().ToString('N')+'.log')
    $arguments=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+$log+'"'),('/ADMINPASSWORDFILE="'+$secret+'"'),('/SERVERNAME="'+$Name+'"'),('/SERVERADDRESS="'+$Url+'"'))
    if ($Port -ne '') { $arguments+=('/HTTPSPORT='+$Port) }
    if ($Firewall -ne '') { $arguments+=('/FIREWALL='+$Firewall) }
    $process=Start-Process -FilePath $Installer -ArgumentList $arguments -PassThru
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
    $script:administratorSession=$session
    $script:administratorCsrf=($management.Content | ConvertFrom-Json).csrfToken
    $theme=Invoke-WebRequest -UseBasicParsing ($address+'theme.js') -WebSession $session
    $themeText=if ($theme.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($theme.Content) } else { [string]$theme.Content }
    if (!$themeText.Contains('localStorage')) { throw 'Installed theme script unavailable.' }
    foreach ($path in @('system','account','audit')) {
        $view=Invoke-WebRequest -UseBasicParsing ($address+$path) -WebSession $session
        if ($view.StatusCode -ne 200) { throw 'Administration page failed.' }
    }
    $diagnostic=Join-Path (Get-Location).Path 'artifacts/server-tests/windows-server-diagnostics.zip'
    Invoke-WebRequest -UseBasicParsing ($address+'api/v1/management/diagnostics') -Method Post -WebSession $session -Headers @{'X-CSRF-TOKEN'=$script:administratorCsrf} -OutFile $diagnostic
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive=[IO.Compression.ZipFile]::OpenRead($diagnostic)
    try { if (!$archive.GetEntry('server.json') -or !$archive.GetEntry('events.json')) { throw 'Diagnostic ZIP missing summary/events.' } }
    finally { $archive.Dispose() }

}
try {
    $invalidRoot=Join-Path $env:TEMP ('onec-invalid-port-'+[Guid]::NewGuid().ToString('N'))
    foreach ($invalidPort in @(0,-1,65536,15433)) {
        $rejected=$false
        try { & (Join-Path $PSScriptRoot '../../deploy/server/windows/Configure-Server.ps1') -InstallDirectory $install -DataDirectory $invalidRoot -HttpsPort $invalidPort }
        catch { $rejected=$true }
        if (!$rejected -or (Test-Path $invalidRoot)) { throw 'Invalid HTTPS port was not rejected before creating server data.' }
    }
    Install-Server -Url 'https://127.0.0.1/monitoring/' -Port '7447' -Firewall '0'
    Health
    Login
    $newPassword=[Guid]::NewGuid().ToString('N')+[Guid]::NewGuid().ToString('N')
    $changed=Invoke-WebRequest -UseBasicParsing ($address+'management/password') -Method Post -WebSession $script:administratorSession -Headers @{'X-CSRF-TOKEN'=$script:administratorCsrf} -Body @{currentPassword=$password;newPassword=$newPassword;confirmation=$newPassword}
    if (![Net.WebUtility]::HtmlDecode($changed.Content).Contains('Пароль изменён')) { throw 'Panel password change did not complete.' }
    $script:password=$newPassword
    Login
    $settings=Get-Content (Join-Path $root 'server.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($settings.Server.DisplayName -ne $displayName -or $settings.Server.PublicAddress -ne $address) { throw 'Name and address were not stored independently.' }
    if ($settings.Urls -ne 'https://+:7447' -or $settings.WindowsInstaller.OpenFirewall -ne $false) { throw 'Separate HTTPS port or unchecked firewall choice was not stored.' }
    if (Get-NetFirewallRule -Name 'OneCBackupServer-Https' -ErrorAction SilentlyContinue) { throw 'Unchecked firewall created an inbound rule.' }

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
    if ($settings.WindowsInstaller.OpenFirewall -ne $false -or (Get-NetFirewallRule -Name 'OneCBackupServer-Https' -ErrorAction SilentlyContinue)) { throw 'Reinstall reopened a disabled firewall choice.' }

    if ($connectionHash -ne (Get-FileHash (Join-Path $root 'connection-string.txt')).Hash -or $certificateHash -ne (Get-FileHash (Join-Path $root 'server.pfx')).Hash) { throw 'Reinstall changed persistent credentials/certificate.' }
    # Emulate a pre-rename SCM registration, then verify upgrade fixes its host path.
    Stop-Service OneCBackupServer
    $registered=Get-CimInstance Win32_Service -Filter "Name='OneCBackupServer'"
    $legacyPath='"'+(Join-Path $install 'OneCBackup.Server.exe')+'" --Server:ConfigFile "'+(Join-Path $root 'server.json')+'"'
    $changed=Invoke-CimMethod -InputObject $registered -MethodName Change -Arguments @{PathName=$legacyPath;DisplayName='1C-Backup Server'}
    if ($changed.ReturnValue -ne 0) { throw 'Could not prepare the legacy service fixture.' }
    Start-Service OneCBackupServer
    Health
    # Upgrade a legacy configuration with only panel-url.txt; do not regenerate its certificate or database.
    $settings.Server.PSObject.Properties.Remove('DisplayName')
    $settings.Server.PSObject.Properties.Remove('PublicAddress')
    $settings.PSObject.Properties.Remove('WindowsInstaller')
    [IO.File]::WriteAllText((Join-Path $root 'server.json'),($settings | ConvertTo-Json -Depth 5),(New-Object Text.UTF8Encoding($false)))
    Install-Server
    Health
    Login
    $settings=Get-Content (Join-Path $root 'server.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($settings.Server.DisplayName -ne $displayName -or $settings.Server.PublicAddress -ne $address) { throw 'Legacy identity migration failed.' }
    if ($settings.WindowsInstaller.OpenFirewall -ne $false -or (Get-NetFirewallRule -Name 'OneCBackupServer-Https' -ErrorAction SilentlyContinue)) { throw 'Legacy migration reopened a removed firewall rule.' }
    $registered=Get-CimInstance Win32_Service -Filter "Name='OneCBackupServer'"
    if ($registered.PathName -notlike '*SPBackup.Server.exe*' -or $registered.DisplayName -ne 'SP Backup Server') { throw 'Upgrade did not migrate the legacy server host path and display name.' }
    Install-Server -Firewall '1'
    Health
    Login
    $rule=Get-NetFirewallRule -Name 'OneCBackupServer-Https'
    $filter=$rule | Get-NetFirewallPortFilter
    if ($rule.Enabled -ne 'True' -or $rule.Direction -ne 'Inbound' -or $rule.Action -ne 'Allow' -or $filter.LocalPort -ne '7447') { throw 'Allowed firewall rule does not target the configured HTTPS port.' }
    if ([string]$rule.Profile -match 'Public|Any') { throw 'Firewall rule unexpectedly opens a public network profile.' }
    # Correct only the application's stale rule and preserve its user-defined remote-address scope.
    $rule | Get-NetFirewallAddressFilter | Set-NetFirewallAddressFilter -RemoteAddress 'LocalSubnet'
    $rule | Get-NetFirewallPortFilter | Set-NetFirewallPortFilter -LocalPort 7448
    Install-Server
    Health
    $fixed=Get-NetFirewallRule -Name 'OneCBackupServer-Https'
    if (($fixed | Get-NetFirewallPortFilter).LocalPort -ne '7447' -or [string]($fixed | Get-NetFirewallAddressFilter).RemoteAddress -ne 'LocalSubnet') { throw 'Firewall repair lost the configured port or custom remote scope.' }
    Install-Server -Firewall '0'
    Health
    Login
    if (Get-NetFirewallRule -Name 'OneCBackupServer-Https' -ErrorAction SilentlyContinue) { throw 'Explicit firewall opt-out did not remove the owned rule.' }

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
    @{ legacyServicePathMigrated=$true; separateHttpsPort=$true; firewallOptOutPreserved=$true; legacyFirewallChoicePreserved=$true; firewallRulePortAndProfiles=$true; firewallRemoteScopePreserved=$true; passwordChangePreservedAfterReinstall=$true; diagnosticZip=$true; administrationPages=$true; separateNameAndAddress=$true; customHttpsPortAndPath=$true; ipCertificateValidated=$true; legacyIdentityMigration=$true; installed=$true; https=$true; administratorLogin=$true; reinstallPreservesData=$true; serviceRestart=$true; uninstallPreservesDatabase=$true; reinstallAfterUninstall=$true } | ConvertTo-Json | Set-Content 'artifacts/server-tests/windows-installer-result.json'
} catch {
    if (Test-Path (Join-Path $root 'setup.log')) { Get-Content (Join-Path $root 'setup.log') }
    Get-Service OneCBackupServer,OneCBackupServerDatabase -ErrorAction SilentlyContinue | Select-Object Name,Status | Format-Table
    throw
} finally {
    Remove-Item -LiteralPath $secret -ErrorAction SilentlyContinue
}
