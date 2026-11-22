param(
    [Parameter(Mandatory=$true)][ValidateSet('source','destination')][string]$Role,
    [Parameter(Mandatory=$true)][string]$Installer,
    [Parameter(Mandatory=$true)][string]$Work
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=Join-Path $env:ProgramData 'OneCBackupServer'
$install=Join-Path $env:ProgramFiles 'SP Backup Server'
$address='https://127.0.0.1:7447/monitoring/'
$fixture=Join-Path $Work 'fixture'
$tool=Join-Path $install 'windows\Server-Snapshot.ps1'
$windowsPowerShell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$identity=(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Cryptography').MachineGuid
$jobName='Cross-host files'
$folderName='Cross-host folder'
$agentName='Cross-host agent'
if ((Test-Path $root) -or (Get-Service OneCBackupServer,OneCBackupServerDatabase -ErrorAction SilentlyContinue)) {
    throw 'Cross-host test requires a clean disposable runner.'
}
New-Item -ItemType Directory -Force -Path $fixture,'artifacts/server-tests' | Out-Null

function Panel-Session([string]$password) {
    $session=New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $page=Invoke-WebRequest ($address+'login') -WebSession $session
    $anti=[Net.WebUtility]::HtmlDecode([regex]::Match($page.Content,'name="__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value)
    $null=Invoke-WebRequest ($address+'session/login') -Method Post -WebSession $session -Body @{name='admin';password=$password;__RequestVerificationToken=$anti}
    if ((Invoke-RestMethod ($address+'api/v1/management/session') -WebSession $session).name -ne 'admin') { throw 'New administrator login failed.' }
    return $session
}

function Server-Json([string]$path,[string]$method,$body,[hashtable]$headers) {
    Invoke-RestMethod ($address+$path) -Method $method -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ($body | ConvertTo-Json -Depth 12 -Compress)
}

function Write-Run([Guid]$runId,[Guid]$jobId,[string]$agentRoute,[hashtable]$headers) {
    $now=[DateTime]::UtcNow
    $null=Server-Json ($agentRoute+'/runs') 'Post' @{
        runId=$runId;jobId=$jobId;jobName=$jobName;startedUtc=$now.AddMinutes(-1).ToString('o');finishedUtc=$now.ToString('o')
        status='Succeeded';warningCount=0;incomplete=$false;destinations=@(@{index=0;succeeded=$true;bytes=256;incomplete=$false})
    } $headers
}

$installPassword=[Guid]::NewGuid().ToString('N')+[Guid]::NewGuid().ToString('N')
$passwordPath=Join-Path $Work 'bootstrap.txt'
[IO.File]::WriteAllText($passwordPath,$installPassword)
try {
    $process=Start-Process -FilePath $Installer -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',
        ('/ADMINPASSWORDFILE="'+$passwordPath+'"'),('/SERVERADDRESS="'+$address+'"'),'/FIREWALL=0') -PassThru
    if (!$process.WaitForExit(240000)) { & taskkill.exe /PID $process.Id /T /F | Out-Null; throw 'Fixture installation timed out.' }
    if ($process.ExitCode -ne 0) { throw 'Fixture installation failed.' }
} finally { Remove-Item -LiteralPath $passwordPath -Force -ErrorAction SilentlyContinue }
if ((Invoke-WebRequest ($address+'health/ready') -TimeoutSec 10).StatusCode -ne 200) { throw 'Initial HTTPS health failed.' }
$session=Panel-Session $installPassword

if ($Role -eq 'source') {
    $agentId=[Guid]::NewGuid()
    $jobId=[Guid]::NewGuid()
    $runId=[Guid]::NewGuid()
    $anti=(Invoke-RestMethod ($address+'api/v1/management/session') -WebSession $session).csrfToken
    $header=@{'X-CSRF-TOKEN'=$anti}
    $folder=Invoke-RestMethod ($address+'api/v1/management/folders') -Method Post -WebSession $session -Headers $header -ContentType 'application/json' -Body (@{name=$folderName} | ConvertTo-Json)
    $invite=Invoke-RestMethod ($address+'api/v1/management/invitations') -Method Post -WebSession $session -Headers $header -ContentType 'application/json' -Body (@{name='cross-host fixture';folderId=$folder.id;agentId=$agentId} | ConvertTo-Json)
    $agent=Server-Json 'api/v1/enrollment' 'Post' @{invitationCode=$invite.code;agentId=$agentId;name=$agentName} @{}
    $agentHeader=@{Authorization=('Bearer '+$agent.token)}
    $header['If-Match']='"1"'
    $job=@{id=$jobId;name=$jobName;source='C:\Data\Fixture';destinationDirectory='D:\FixtureBackups';
        scheduleKind='Weekly';daysOfWeek='Monday, Friday';timeZoneId='Asia/Vladivostok';enabled=$true;
        fileBackupMode='Incremental';fullBackupDayOfWeek='Sunday';additionalDailyTimes=@('12:00:00')}
    $saved=Invoke-RestMethod ($address+'api/v1/management/agents/'+$agentId+'/configuration') -Method Put -WebSession $session -Headers $header -ContentType 'application/json' -Body (@{agentId=$agentId;protocolVersion=1;jobs=@($job)} | ConvertTo-Json -Depth 10)
    if ($saved.revision -ne 2 -or $saved.jobs[0].id -ne $jobId) { throw 'Could not seed the server plan.' }
    $agentRoute='api/v1/agents/'+$agentId
    $null=Server-Json ($agentRoute+'/monitoring') 'Put' @{sentUtc=[DateTime]::UtcNow.ToString('o');agentVersion='migration-fixture';pendingRuns=0;
        jobs=@(@{jobId=$jobId;name=$jobName;sourceKind='FilesAndFolders';enabled=$true;schedule='Weekly';destinationCount=1})} $agentHeader
    Write-Run $runId $jobId $agentRoute $agentHeader
    $snapshot=Join-Path $fixture 'snapshot'
    & $windowsPowerShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $tool -DataDirectory $root -InstallDirectory $install -Action Backup -BackupDirectory $snapshot | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Source cold snapshot failed.' }
    $manifest=Get-Content (Join-Path $snapshot 'snapshot.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.Format -ne 2 -or $manifest.Machine -ne $identity) { throw 'Unexpected source snapshot identity.' }
    Copy-Item (Join-Path $root 'server.cer') (Join-Path $fixture 'source.cer')
    $cookies=@(foreach ($cookie in $session.Cookies.GetCookies([Uri]$address)) {
        if ($cookie.Name.StartsWith('OneCBackup.Server.Session')) { @{name=$cookie.Name;value=$cookie.Value;path=$cookie.Path} }
    })
    if (!$cookies.Count) { throw 'Expected an authenticated source cookie.' }
    $metadata=@{machine=$identity;adminPassword=$installPassword;agentToken=$agent.token;agentId=$agentId;
        jobId=$jobId;runId=$runId;folderId=$folder.id;cookies=$cookies;certificateSha256=(Get-FileHash (Join-Path $fixture 'source.cer')).Hash}
    $metadata | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $fixture 'fixture.json') -Encoding utf8
    $null=Invoke-RestMethod ($address+'api/v1/management/folders') -Method Post -WebSession $session -Headers $header -ContentType 'application/json' -Body (@{name='Created after cold snapshot'} | ConvertTo-Json)
    return
}

$meta=Get-Content (Join-Path $fixture 'fixture.json') -Raw | ConvertFrom-Json
$snapshot=Join-Path $fixture 'snapshot'
$manifest=Get-Content (Join-Path $snapshot 'snapshot.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($identity -eq $meta.machine -or $manifest.Machine -ne $meta.machine -or $manifest.Format -ne 2) { throw 'The source and destination must be different Windows machines.' }
$sourceCert=Join-Path $fixture 'source.cer'
if ((Get-FileHash $sourceCert).Hash -ne $meta.certificateSha256) { throw 'The source public certificate does not match the sealed fixture.' }
Import-Certificate -FilePath $sourceCert -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
& $windowsPowerShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $tool -DataDirectory $root -InstallDirectory $install -Action RestoreOnNewComputer -ConfirmNewComputer -BackupDirectory $snapshot | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Recovery on the destination failed.' }
if ((Invoke-WebRequest ($address+'health/ready') -TimeoutSec 10).StatusCode -ne 200) { throw 'Restored HTTPS readiness failed.' }
$old=New-Object Microsoft.PowerShell.Commands.WebRequestSession
foreach ($item in $meta.cookies) { $old.Cookies.Add([Uri]$address,[System.Net.Cookie]::new($item.name,$item.value,$item.path)) }
$status=(Invoke-WebRequest ($address+'api/v1/management/session') -WebSession $old -SkipHttpErrorCheck).StatusCode
if ($status -ne 401) { throw 'The source session was accepted on the destination.' }
$restored=Panel-Session $meta.adminPassword
$agentId=$meta.agentId
$adminRoute=$address+'api/v1/management/agents/'+$agentId
$folders=Invoke-RestMethod ($address+'api/v1/management/folders') -WebSession $restored
if ($folderName -notin @($folders.name) -or 'Created after cold snapshot' -in @($folders.name)) { throw 'Restored folder contents differ.' }
$plan=Invoke-RestMethod ($adminRoute+'/configuration') -WebSession $restored
if ($plan.revision -ne 2 -or $plan.jobs[0].id -ne $meta.jobId -or $plan.jobs[0].name -ne $jobName) { throw 'Restored server plan differs.' }
function Assert-Run([string]$runId) {
    $html=(Invoke-WebRequest ($address+'agents/'+$agentId+'?RunId='+$runId) -WebSession $restored).Content
    if (![Net.WebUtility]::HtmlDecode($html).Contains('Доставлено и проверено · 256 байт')) {
        throw 'Expected run is missing from the restored history.'
    }
}
Assert-Run $meta.runId
$agentRoute='api/v1/agents/'+$agentId
$agentHeader=@{Authorization=('Bearer '+$meta.agentToken);'SP-Backup-Capabilities'='configuration-v1,strict-configuration-v1,managed-execution-v1'}
$agentPlan=Invoke-RestMethod ($address+$agentRoute+'/configuration') -Headers $agentHeader
if ($agentPlan.revision -ne 2 -or $agentPlan.jobs[0].id -ne $meta.jobId) { throw 'Agent did not receive its restored plan.' }
$null=Server-Json ($agentRoute+'/heartbeat') 'Post' @{agentId=$agentId;sentUtc=[DateTime]::UtcNow.ToString('o');agentVersion='after-recovery';configurationRevision=2} $agentHeader
$newRun=[Guid]::NewGuid()
Write-Run $newRun $meta.jobId $agentRoute $agentHeader
Assert-Run ($newRun.ToString())
Assert-Run $meta.runId
if (!(Get-ChildItem ($root+'.before-restore-*') -Directory)) { throw 'Pre-restore destination data was not retained.' }
$oldKeys=@(Get-ChildItem (Join-Path $snapshot 'data\data\keys') -File | ForEach-Object Name)
$newKeys=@(Get-ChildItem (Join-Path $root 'data\keys') -File | ForEach-Object Name)
if (!$oldKeys.Count -or !$newKeys.Count -or @($newKeys | Where-Object { $oldKeys -contains $_ }).Count) { throw 'Destination key rotation was not confirmed.' }
$schemaSource=Get-Content 'src/SPBackup.Server/ServerStore.cs' -Raw
if ($schemaSource -notmatch 'const int SchemaVersion[ ]*=[ ]*([0-9]+)') { throw 'Unknown expected schema.' }
$schema=[int]$Matches[1]
$system=(Invoke-WebRequest ($address+'system') -WebSession $restored).Content
if ($system -notmatch ('Версия схемы</dt><dd>'+ $schema +'</dd>')) { throw 'Target schema is not ready.' }
@{success=$true; independentWindowsHosts=$true; schema=$schema; run=$env:GITHUB_RUN_ID; source=$env:GITHUB_SHA;
    installerSha256=(Get-FileHash $Installer -Algorithm SHA256).Hash; https=$true; oldSessionRejected=$true;
    administratorLogin=$true; previousAndNewReport=$true; planPreserved=$true; enrolledAgentTokenPreserved=$true;
    dataRetained=$true; dpapiKeyRotated=$true} | ConvertTo-Json | Set-Content 'artifacts/server-tests/windows-cross-host-result.json'
