# Called only by the isolated native Windows installer fixture, never on a user's server.
param([string]$Root,[string]$Install,[string]$Address,[string]$Installer,[string]$PasswordFile)
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSEdition -eq 'Desktop') { $env:PSModulePath=Join-Path $PSHOME 'Modules' }
[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
Set-StrictMode -Version Latest
$Session=New-Object Microsoft.PowerShell.Commands.WebRequestSession
$page=Invoke-WebRequest -UseBasicParsing ($Address+'login') -WebSession $Session
$anti=[Net.WebUtility]::HtmlDecode([regex]::Match($page.Content,'name="__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value)
$null=Invoke-WebRequest -UseBasicParsing ($Address+'session/login') -Method Post -WebSession $Session -Body @{name='admin';password=[IO.File]::ReadAllText($PasswordFile);__RequestVerificationToken=$anti}
$sessionInfo=Invoke-RestMethod -Uri ($Address+'api/v1/management/session') -WebSession $Session
$Csrf=$sessionInfo.csrfToken
function Install-Server {
    $process=Start-Process -FilePath $Installer -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -PassThru
    if (!$process.WaitForExit(60000)) { throw 'Busy installer did not exit within one minute.' }
    if ($process.ExitCode -ne 0) { throw 'Installer rejected active maintenance.' }
}
$tool=Join-Path $Install 'windows\Server-Snapshot.ps1'
$parent=Join-Path $env:TEMP ('sp-server-snapshot-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($parent)
$snapshot=Join-Path $parent 'saved'
function Post([string]$Path,$Body) {
    Invoke-RestMethod -Uri ($Address+$Path) -Method Post -WebSession $Session -Headers @{'X-CSRF-TOKEN'=$Csrf} -ContentType 'application/json; charset=utf-8' -Body ($Body | ConvertTo-Json -Depth 5 -Compress)
}
function Rejected($Operation) { $failed=$false; try { & $Operation | Out-Null } catch { $failed=$true }; if (!$failed) { throw 'Unsafe snapshot operation was accepted.' } }
$folder=Post 'api/v1/management/folders' @{name='До снимка'}
$agentId=[Guid]::NewGuid()
$invitation=Post 'api/v1/management/invitations' @{name='snapshot fixture';agentId=$agentId;folderId=$folder.id}
$agent=Invoke-RestMethod -Uri ($Address+'api/v1/enrollment') -Method Post -ContentType 'application/json; charset=utf-8' -Body (@{invitationCode=$invitation.code;agentId=$agentId;name='snapshot-agent'} | ConvertTo-Json)
$agentHeaders=@{Authorization=('Bearer '+$agent.token)}
Invoke-WebRequest -UseBasicParsing ($Address+'management/time-zone') -Method Post -WebSession $Session -Headers @{'X-CSRF-TOKEN'=$Csrf} -Body @{timeZoneId='Asia/Vladivostok'} | Out-Null
$timeHash=(Get-FileHash (Join-Path $Root 'data\panel-time-zone.json')).Hash
# Installer must refuse a snapshot's mutex before stopping services or replacing binaries.
$mutex=New-Object Threading.Mutex($false,'Global\SPBackupServerInstall')
try {
    [void]$mutex.WaitOne(0)
    $pidBefore=(Get-CimInstance Win32_Service -Filter "Name='OneCBackupServer'").ProcessId
    Rejected { & $tool -Action Backup -BackupDirectory (Join-Path $parent 'locked') }
    Rejected { Install-Server }
    if ((Test-Path (Join-Path $parent 'locked')) -or (Get-CimInstance Win32_Service -Filter "Name='OneCBackupServer'").ProcessId -ne $pidBefore) { throw 'Busy maintenance changed the server.' }
} finally { $mutex.ReleaseMutex(); $mutex.Dispose() }
Rejected { & $tool -Action Backup -BackupDirectory (Join-Path $Root 'nested-copy') }
$result=& $tool -Action Backup -BackupDirectory $snapshot
if (!$result.Success -or !(Test-Path (Join-Path $snapshot 'snapshot.json'))) { throw 'Cold snapshot was not completed.' }
& $tool -Action Verify -BackupDirectory $snapshot | Out-Null
foreach ($name in @('OneCBackupServer','OneCBackupServerDatabase')) { if ((Get-Service $name).Status -ne 'Running') { throw 'Backup did not resume the original services.' } }
$owner=(Get-Acl $snapshot).GetOwner([Security.Principal.SecurityIdentifier]).Value
if ($owner -ne 'S-1-5-32-544') { throw 'Snapshot owner is not Administrators.' }
if ((Get-Acl $snapshot).Access | Where-Object { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -notin @('S-1-5-18','S-1-5-32-544') }) { throw 'Snapshot ACL exposes secrets.' }
Rejected { & $tool -Action Backup -BackupDirectory $snapshot }
# Corrupt and foreign-machine copies are rejected while the running server stays untouched.
$pidBefore=(Get-CimInstance Win32_Service -Filter "Name='OneCBackupServer'").ProcessId
$file=Join-Path $snapshot 'data\panel-url.txt'; $bytes=[IO.File]::ReadAllBytes($file)
try { [IO.File]::AppendAllText($file,'changed'); Rejected { & $tool -Action Restore -BackupDirectory $snapshot } }
finally { [IO.File]::WriteAllBytes($file,$bytes) }
$manifest=Join-Path $snapshot 'snapshot.json'; $bytes=[IO.File]::ReadAllBytes($manifest)
try {
    $wrong=Get-Content $manifest -Raw -Encoding UTF8 | ConvertFrom-Json; $wrong.Machine='another-machine'
    [IO.File]::WriteAllText($manifest,($wrong | ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
    Rejected { & $tool -Action Restore -BackupDirectory $snapshot }
} finally { [IO.File]::WriteAllBytes($manifest,$bytes) }
if ((Get-CimInstance Win32_Service -Filter "Name='OneCBackupServer'").ProcessId -ne $pidBefore) { throw 'Invalid snapshot interrupted the running server.' }
$null=Post 'api/v1/management/folders' @{name='После снимка'}
Invoke-WebRequest -UseBasicParsing ($Address+'management/time-zone') -Method Post -WebSession $Session -Headers @{'X-CSRF-TOKEN'=$Csrf} -Body @{timeZoneId='UTC'} | Out-Null
# A checksummed but unusable certificate must trigger rollback to the live state.
$cert=Join-Path $snapshot 'data\server.pfx'; $certBytes=[IO.File]::ReadAllBytes($cert)
$manifestBytes=[IO.File]::ReadAllBytes($manifest)
try {
    [IO.File]::WriteAllBytes($cert,[byte[]]@(1,2,3,4))
    $bad=Get-Content $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
    $certEntry=$bad.Files | Where-Object Path -eq 'server.pfx'
    $certEntry.Length=4; $certEntry.Sha256=(Get-FileHash $cert -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($manifest,($bad | ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
    Rejected { & $tool -Action Restore -BackupDirectory $snapshot }
    $null=Invoke-RestMethod -Uri ($Address+'api/v1/management/session') -WebSession $Session
    $afterFailure=@(Invoke-RestMethod -Uri ($Address+'api/v1/management/folders') -WebSession $Session)
    if ('После снимка' -notin @($afterFailure.name)) { throw 'Failed restore did not return to the live database.' }
} finally { [IO.File]::WriteAllBytes($cert,$certBytes); [IO.File]::WriteAllBytes($manifest,$manifestBytes) }
$restored=& $tool -Action Restore -BackupDirectory $snapshot
if (!$restored.Success -or !(Test-Path -LiteralPath $restored.PreviousData)) { throw 'Restore did not retain the previous data folder.' }
# Existing panel cookie proves restored DPAPI keys; bearer request proves persisted database credentials.
$null=Invoke-RestMethod -Uri ($Address+'api/v1/management/session') -WebSession $Session
$folders=@(Invoke-RestMethod -Uri ($Address+'api/v1/management/folders') -WebSession $Session)
if ('До снимка' -notin @($folders.name) -or 'После снимка' -in @($folders.name)) { throw 'Database did not return to its saved state.' }
if ((Get-FileHash (Join-Path $Root 'data\panel-time-zone.json')).Hash -ne $timeHash) { throw 'Server settings did not return to their saved state.' }
$null=Invoke-RestMethod -Uri ($Address+'api/v1/agents/'+$agentId+'/configuration') -Headers $agentHeaders
& $tool -Action Verify -BackupDirectory $snapshot | Out-Null
@{coldBackup=$true; checksumVerification=$true; sameMachineGuard=$true; corruptedCopyRejectedWithoutStopping=$true; noOverwrite=$true; nestedPathRejected=$true; installerMutex=$true; privateSnapshotAcl=$true; startupFailureRollback=$true; restoredDatabase=$true; restoredSettings=$true; restoredDpapiCookie=$true; agentTokenPreserved=$true; previousDataRetained=$true} | ConvertTo-Json | Set-Content 'artifacts/server-tests/windows-snapshot-result.json'
