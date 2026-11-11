# Isolated GitHub Windows runner only. Never run against an existing installation.
param([Parameter(Mandatory=$true)][string]$Installer,[Parameter(Mandatory=$true)][string]$BaselineInstaller)
$ErrorActionPreference='Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or (Test-Path (Join-Path $env:ProgramData 'OneCBackupServer'))) { throw 'Upgrade fixture requires a clean disposable GitHub runner.' }
if ($PSVersionTable.PSEdition -eq 'Desktop') { $env:PSModulePath=Join-Path $PSHOME 'Modules' }
[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
$root=Join-Path $env:ProgramData 'OneCBackupServer';$install=Join-Path $env:ProgramFiles 'SP Backup Server'
$parent=Join-Path $env:TEMP ('sp-upgrade-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($parent)
$secret=Join-Path $parent 'password.txt';$password=[Guid]::NewGuid().ToString('N')+[Guid]::NewGuid().ToString('N')
[IO.File]::WriteAllText($secret,$password)
$url='https://127.0.0.1:7447/monitoring/'
$tool=(Resolve-Path (Join-Path $PSScriptRoot '../../deploy/server/windows/Server-Snapshot.ps1')).Path
$statusTool=(Resolve-Path (Join-Path $PSScriptRoot '../../deploy/server/windows/Server-Status.ps1')).Path
function Install([string]$Path,[bool]$Failure=$false) {
    $log=Join-Path (Get-Location).Path ('artifacts/server-tests/windows-installer-upgrade-'+[Guid]::NewGuid().ToString('N')+'.log')
    $process=Start-Process $Path -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+$log+'"'),('/ADMINPASSWORDFILE="'+$secret+'"'),'/SERVERNAME="Upgrade fixture"',('/SERVERADDRESS="'+$url+'"'),'/FIREWALL=0') -PassThru
    if (!$process.WaitForExit(240000)) { & taskkill.exe /PID $process.Id /T /F | Out-Null; throw 'Upgrade installer timed out.' }
    if (($process.ExitCode -ne 0) -ne $Failure) { throw ('Unexpected upgrade installer exit '+$process.ExitCode) }
}
function Sql([string]$Command) {
    $old=$env:PGPASSWORD;$timeout=$env:PGCONNECT_TIMEOUT
    try {
        $env:PGCONNECT_TIMEOUT='5';$env:PGPASSWORD=[IO.File]::ReadAllText((Join-Path $root 'connection-string.txt')).Trim().Substring('Host=127.0.0.1;Port=15433;Database=postgres;Username=onecbackup;Password='.Length)
        $value=& (Join-Path $install 'postgresql/bin/psql.exe') '-X' '-w' '-h' '127.0.0.1' '-p' '15433' '-U' 'onecbackup' '-d' 'postgres' '-At' '-v' 'ON_ERROR_STOP=1' '-c' $Command
        if ($LASTEXITCODE -ne 0) { throw 'Upgrade fixture SQL failed.' };return $value
    } finally { $env:PGPASSWORD=$old;$env:PGCONNECT_TIMEOUT=$timeout }
}
function Rejected($Operation) { $failed=$false;try { & $Operation | Out-Null } catch { $failed=$true };if (!$failed) { throw 'Unsafe upgrade action accepted.' } }
function Session {
    $script:session=New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $page=Invoke-WebRequest -UseBasicParsing ($url+'login') -WebSession $session
    $csrf=[Net.WebUtility]::HtmlDecode([regex]::Match($page.Content,'name="__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value)
    $null=Invoke-WebRequest -UseBasicParsing ($url+'session/login') -Method Post -WebSession $session -Body @{name='admin';password=$password;__RequestVerificationToken=$csrf}
    return Invoke-RestMethod ($url+'api/v1/management/session') -WebSession $session
}
function Post([string]$Path,$Body) { Invoke-RestMethod ($url+$Path) -Method Post -WebSession $session -Headers @{'X-CSRF-TOKEN'=$info.csrfToken} -ContentType 'application/json; charset=utf-8' -Body ($Body | ConvertTo-Json -Depth 5) }
function Assert-Preserved([int]$Schema) {
    $ready=$false
    for ($attempt=0;$attempt -lt 30;$attempt++) {
        try { if ((Invoke-WebRequest -UseBasicParsing ($url+'health/ready') -TimeoutSec 3).StatusCode -eq 200) { $ready=$true;break } } catch { }
        Start-Sleep -Seconds 2
    }
    if (!$ready) { throw 'Upgraded/restored HTTPS endpoint is not ready.' }
    if ((Sql 'SELECT max(version) FROM server_schema;') -ne [string]$Schema) { throw 'Unexpected schema version.' }
    $null=Invoke-RestMethod ($url+'api/v1/management/session') -WebSession $session
    $null=Invoke-RestMethod ($url+'api/v1/agents/'+$id+'/configuration') -Headers @{Authorization=('Bearer '+$agent.token)}
    foreach ($name in $hashes.Keys) { if ((Get-FileHash (Join-Path $root $name)).Hash -ne $hashes[$name]) { throw ('Upgrade changed '+$name) } }
    $folders=Invoke-RestMethod ($url+'api/v1/management/folders') -WebSession $session
    if ('Before upgrade' -notin @($folders.name)) { throw 'Upgrade lost persistent database content.' }
    $report=& $statusTool
    if (!$report.Healthy) { throw ('Local readiness diagnostics failed: '+($report.Checks | ConvertTo-Json -Compress)) }
}
New-Item -ItemType Directory 'artifacts/server-tests' -Force | Out-Null
Install $BaselineInstaller
$info=Session
if ((Sql 'SELECT max(version) FROM server_schema;') -ne '5') { throw 'Pinned public baseline must use schema 5.' }
$folder=Post 'api/v1/management/folders' @{name='Before upgrade'};$id=[Guid]::NewGuid()
$invitation=Post 'api/v1/management/invitations' @{name='upgrade fixture';agentId=$id;folderId=$folder.id}
$agent=Invoke-RestMethod ($url+'api/v1/enrollment') -Method Post -ContentType 'application/json' -Body (@{invitationCode=$invitation.code;agentId=$id;name='upgrade-agent'} | ConvertTo-Json)
$hashes=@{};foreach ($name in @('server.json','connection-string.txt','server.pfx','certificate-password.txt')) { $hashes[$name]=(Get-FileHash (Join-Path $root $name)).Hash }
$oldDll=(Get-FileHash (Join-Path $install 'SPBackup.Server.dll')).Hash
$bundle=Join-Path $parent 'before-upgrade'
$null=& $tool -Action PrepareUpgrade -BackupDirectory $bundle -InstallDirectory $install
$null=& $tool -Action VerifyUpgrade -BackupDirectory $bundle -InstallDirectory $install
# Full-program guard includes maintenance scripts even when the three legacy identity files are unchanged.
$dependency=Join-Path $install 'windows/Open-Panel.ps1';$dependencyBytes=[IO.File]::ReadAllBytes($dependency)
try { [IO.File]::AppendAllText($dependency,'changed');Rejected { & $tool -Action Verify -BackupDirectory $bundle -InstallDirectory $install } }
finally { [IO.File]::WriteAllBytes($dependency,$dependencyBytes) }
Install $Installer
Assert-Preserved 6
Rejected { & $tool -Action Restore -BackupDirectory $bundle -InstallDirectory $install }
$recovery=Join-Path $bundle 'Server-Snapshot.ps1'
# Valid hashes do not guarantee successful startup: an unusable PFX must roll back BOTH live trees.
$manifest=Join-Path $bundle 'snapshot.json';$manifestBytes=[IO.File]::ReadAllBytes($manifest)
$pfx=Join-Path $bundle 'data/server.pfx';$pfxBytes=[IO.File]::ReadAllBytes($pfx)
$newDll=(Get-FileHash (Join-Path $install 'SPBackup.Server.dll')).Hash
try {
    [IO.File]::WriteAllBytes($pfx,[byte[]]@(1,2,3,4))
    $bad=Get-Content $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
    $entry=$bad.Files | Where-Object Path -eq 'server.pfx';$entry.Length=4;$entry.Sha256=(Get-FileHash $pfx).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($manifest,($bad | ConvertTo-Json -Depth 6))
    Rejected { & $recovery -Action RollbackUpgrade -BackupDirectory $bundle -InstallDirectory $install }
    if ((Get-FileHash (Join-Path $install 'SPBackup.Server.dll')).Hash -ne $newDll) { throw 'Failed rollback left the wrong program installed.' }
    Assert-Preserved 6
} finally { [IO.File]::WriteAllBytes($pfx,$pfxBytes);[IO.File]::WriteAllBytes($manifest,$manifestBytes) }
$result=& $recovery -Action RollbackUpgrade -BackupDirectory $bundle -InstallDirectory $install
if (!$result.Success -or !(Test-Path $result.PreviousProgram) -or !(Test-Path $result.PreviousData) -or (Get-FileHash (Join-Path $install 'SPBackup.Server.dll')).Hash -ne $oldDll) { throw 'Full upgrade rollback did not restore and retain both trees.' }
Assert-Preserved 5
# Conflict makes the real 5 -> 6 migration fail transactionally; saved baseline stays usable.
Stop-Service OneCBackupServer
$null=Sql 'CREATE TABLE server_remote_runs (fixture integer);'
Install $Installer $true
if ((Sql 'SELECT max(version) FROM server_schema;') -ne '5') { throw 'Failed migration committed schema version.' }
if ((Get-Service OneCBackupServer).Status -ne 'Stopped') { throw 'Failed migration started the application.' }
$report=& $statusTool
if ($report.Healthy -or @($report.Checks | Where-Object { $_.Code -eq 'OneCBackupServer' -and $_.State -eq 'Error' }).Count -ne 1) { throw 'Stopped application was not diagnosed.' }
$null=& $recovery -Action RollbackUpgrade -BackupDirectory $bundle -InstallDirectory $install
Assert-Preserved 5
# Report a stopped database without starting it or changing the configured secrets.
& (Join-Path $install 'windows/Stop-Server.ps1') | Out-Null
$report=& $statusTool
if ($report.Healthy -or (Get-Service OneCBackupServerDatabase).Status -ne 'Stopped' -or @($report.Checks | Where-Object State -eq 'Error').Count -lt 2) { throw 'Local diagnostics modified stopped services or hid the fault.' }
Start-Service OneCBackupServerDatabase;Start-Service OneCBackupServer
Assert-Preserved 5
# A replaced public CER is diagnosed independently of the certificate loaded by the running service.
$faultRoot=Join-Path $parent 'status-fixture';[void][IO.Directory]::CreateDirectory($faultRoot)
Copy-Item (Join-Path $root 'server.json') (Join-Path $faultRoot 'server.json')
$expired=New-SelfSignedCertificate -DnsName 'localhost' -CertStoreLocation 'Cert:\CurrentUser\My' -NotBefore (Get-Date).AddDays(-10) -NotAfter (Get-Date).AddDays(-1)
try {
    Export-Certificate -Cert $expired -FilePath (Join-Path $faultRoot 'server.cer') | Out-Null
    $report=& $statusTool -DataDirectory $faultRoot
    if ($report.Healthy -or @($report.Checks | Where-Object { $_.Code -eq 'CertificateFile' -and $_.State -eq 'Error' }).Count -ne 1 -or @($report.Checks | Where-Object { $_.Code -eq 'HttpsReadiness' -and $_.State -eq 'Healthy' }).Count -ne 1) { throw 'Expired CER or independent trusted readiness was not diagnosed.' }
} finally { Remove-Item ('Cert:\CurrentUser\My\'+$expired.Thumbprint) }
$fault=Get-Content (Join-Path $faultRoot 'server.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$fault.Server.DataDirectory='?:\missing'
[IO.File]::WriteAllText((Join-Path $faultRoot 'server.json'),($fault | ConvertTo-Json -Depth 6))
$report=& $statusTool -DataDirectory $faultRoot
if (@($report.Checks | Where-Object { $_.Code -eq 'DataDisk' -and $_.State -eq 'Unknown' }).Count -ne 1) { throw 'Unreadable data volume was shown as healthy.' }
# Clean only this fixture, after the real retained-data uninstall has succeeded.
$uninstall=Start-Process (Join-Path $install 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -Wait -PassThru
if ($uninstall.ExitCode -ne 0 -or (Get-Service OneCBackupServer -ErrorAction SilentlyContinue) -or (Get-Service OneCBackupServerDatabase -ErrorAction SilentlyContinue)) { throw 'Fixture cleanup did not remove the owned services.' }
Remove-Item -LiteralPath $root -Recurse -Force
Remove-Item -LiteralPath $secret
@{baselinePreview8=$true; schema5To6=$true; persistedDatabase=$true; preservedConfigurationAndSecrets=$true; preservedDpapiCookie=$true; preservedAgentToken=$true; fullProgramInventory=$true; ordinaryRestoreRejectsOtherVersion=$true; fullProgramAndDataRollback=$true; failedRollbackRestoresBothTrees=$true; migrationFailureRetainsSchema=$true; migrationFailureRecovery=$true; stoppedServicesDiagnosedReadOnly=$true; expiredCertificateDiagnosed=$true; activeHttpsIndependentOfCer=$true; unreadableVolumeDiagnosed=$true} | ConvertTo-Json | Set-Content 'artifacts/server-tests/windows-upgrade-result.json'
