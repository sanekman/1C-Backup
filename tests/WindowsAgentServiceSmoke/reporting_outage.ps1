param([Parameter(Mandatory)][string]$Agent, [Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or (Test-Path (Join-Path $env:ProgramData 'OneCBackupServer')) -or
    (Get-Service OneCBackupServer,OneCBackupServerDatabase -ErrorAction SilentlyContinue)) {
    throw 'Reporting fixture requires a clean disposable runner.'
}
$lab = Join-Path $env:RUNNER_TEMP ('spbackup-reporting-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $lab,$Output | Out-Null
$passwordFile = Join-Path $lab 'bootstrap.txt'
$password = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
$address = 'https://127.0.0.1:7447/monitoring/'
$ftp = $null
$probe = $null
$serverStopped = $false
$stage = 'install-server'

function Wait-Until([scriptblock]$Check, [string]$Message, [int]$Seconds = 60) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if ($script:probe -and $script:probe.HasExited) { throw 'The installed-agent fixture exited prematurely.' }
        if (& $Check) { return }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $Message
}

try {
    $name = 'SP-Backup-Server-0.1.0-preview.18-windows-x64-setup.exe'
    gh release download server-v0.1.0-preview.18 -p $name -D $lab
    if ($LASTEXITCODE -ne 0) { throw 'Server installer download failed.' }
    $installer = Join-Path $lab $name
    if ((Get-FileHash -LiteralPath $installer).Hash -ne '076fb3060293292c81cdaf99ac240d8a2fcb61c9b0324df46416199cb3c960bc') {
        throw 'Server installer hash differs from the accepted preview.18.'
    }
    [IO.File]::WriteAllText($passwordFile, $password)
    $setup = Start-Process $installer -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',
        ('/ADMINPASSWORDFILE="' + $passwordFile + '"'), ('/SERVERADDRESS="' + $address + '"'), '/FIREWALL=0') -PassThru
    if (!$setup.WaitForExit(240000) -or $setup.ExitCode -ne 0) { throw 'Server fixture installation failed.' }
    Remove-Item -LiteralPath $passwordFile -Force
    $stage = 'enroll-and-connect'
    $session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
    $page = Invoke-WebRequest ($address + 'login') -WebSession $session
    $anti = [Net.WebUtility]::HtmlDecode([regex]::Match($page.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value)
    $null = Invoke-WebRequest ($address + 'session/login') -Method Post -WebSession $session -Body @{
        name = 'admin'; password = $password; __RequestVerificationToken = $anti
    }
    $admin = Invoke-RestMethod ($address + 'api/v1/management/session') -WebSession $session
    $agentId = [Guid]::NewGuid()
    $invite = Invoke-RestMethod ($address + 'api/v1/management/invitations') -Method Post -WebSession $session -Headers @{
        'X-CSRF-TOKEN' = $admin.csrfToken
    } -ContentType 'application/json' -Body (@{name='outage fixture';agentId=$agentId} | ConvertTo-Json)
    $enrolled = Invoke-RestMethod ($address + 'api/v1/enrollment') -Method Post -ContentType 'application/json' -Body (@{
        name='outage fixture';agentId=$agentId;invitationCode=$invite.code
    } | ConvertTo-Json)
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new((Join-Path $env:ProgramData 'OneCBackupServer/server.cer'))
    try { $pin = $certificate.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256) }
    finally { $certificate.Dispose() }
    $ftpPassword = [Guid]::NewGuid().ToString('N')
    $ftpSettings = Join-Path $lab 'ftp-settings.json'
    @{ftpPassword=$ftpPassword} | ConvertTo-Json | Set-Content -LiteralPath $ftpSettings -Encoding ascii
    $ftp = Start-Process python -ArgumentList @('tests/ftp/gated_delivery.py', ('"' + $lab + '"')) -RedirectStandardInput $ftpSettings -PassThru
    Wait-Until { Test-Path (Join-Path $lab 'port') } 'Loopback FTP fixture did not start.'
    @{address=$address; agentId=$agentId; token=$enrolled.token; pin=$pin; ftpPassword=$ftpPassword;
        ftpPort=[int](Get-Content (Join-Path $lab 'port') -Raw)} |
        ConvertTo-Json | Set-Content (Join-Path $lab 'agent-settings.json') -Encoding utf8
    $probe = Start-Process dotnet -ArgumentList @('run','--project','tests/WindowsAgentServiceSmoke','-c','Release','--',
        ('"' + $Agent + '"'), ('"' + $lab + '"'), '--verify-reporting-outage') -PassThru
    Wait-Until { Test-Path (Join-Path $lab 'agent-ready') } 'Installed agent did not become ready.' 150
    $route = $address + 'api/v1/management/agents/' + $agentId
    Wait-Until { (Invoke-RestMethod ($route + '/monitoring') -WebSession $session).receivedUtc } 'Initial agent report did not reach the server.'
    New-Item -ItemType File -Path (Join-Path $lab 'start-backup') | Out-Null
    $stage = 'disconnect-during-upload'
    Wait-Until { Test-Path (Join-Path $lab 'upload-started') } 'Copy did not reach the gated FTP destination.'
    Stop-Service OneCBackupServer
    $serverStopped = $true
    if ((Get-Service OneCBackupServer).Status -ne 'Stopped' -or (Test-Path (Join-Path $lab 'local-result.json'))) {
        throw 'Server did not stop while the copy was still active.'
    }
    New-Item -ItemType File -Path (Join-Path $lab 'release-upload') | Out-Null
    Wait-Until { Test-Path (Join-Path $lab 'local-result.json') } 'No verified local backup completed during the outage.'
    $result = Get-Content (Join-Path $lab 'local-result.json') -Raw | ConvertFrom-Json
    if ((Get-Service OneCBackupServer).Status -ne 'Stopped') { throw 'Server restarted before the local backup completed.' }
    $stage = 'verify-report-after-reconnect'
    Start-Service OneCBackupServer
    $serverStopped = $false
    Wait-Until {
        try { (Invoke-WebRequest ($address + 'health/ready') -TimeoutSec 3).StatusCode -eq 200 }
        catch { $false }
    } 'Monitoring server did not become ready.'
    Wait-Until { @((Invoke-RestMethod ($route + '/runs') -WebSession $session).runs).Count -gt 0 } 'The offline run did not reach the server.'
    $first = (Invoke-RestMethod ($route + '/monitoring') -WebSession $session).receivedUtc
    Wait-Until {
        $now = Invoke-RestMethod ($route + '/monitoring') -WebSession $session
        $now.receivedUtc -gt $first -and $now.snapshot.pendingRuns -eq 0
    } 'The agent did not send another snapshot with an empty queue.'
    $runs = @((Invoke-RestMethod ($route + '/runs') -WebSession $session).runs)
    if ($runs.Count -ne 1 -or $runs[0].runId -ne $result.runId -or $runs[0].jobId -ne $result.jobId -or
        $runs[0].status -ne 'Succeeded' -or $runs[0].incomplete -or @($runs[0].destinations).Count -ne 2 -or
        @($runs[0].destinations | Where-Object { !$_.succeeded -or $_.incomplete -or $_.bytes -ne $result.bytes }).Count -ne 0 -or
        @($runs[0].destinations.index | Sort-Object -Unique).Count -ne 2) {
        throw 'The server history differs from the verified local run.'
    }
    New-Item -ItemType File -Path (Join-Path $lab 'release-agent') | Out-Null
    if (!$probe.WaitForExit(30000) -or $probe.ExitCode -ne 0 -or !(Test-Path (Join-Path $lab 'same-session'))) {
        throw 'The installed-agent session or cleanup could not be verified.'
    }
    @{success=$true; stage='passed'; runId=$result.runId; deliveries=2; restoredDuringOutage=$true;
        sameAgentSession=$true; serverVersion='0.1.0-preview.18'; observedReportingCycle=$true} |
        ConvertTo-Json | Set-Content (Join-Path $Output 'reporting-outage-result.json')
}
catch {
    @{success=$false;stage=$stage} | ConvertTo-Json | Set-Content (Join-Path $Output 'reporting-outage-result.json')
    throw
}
finally {
    if ($serverStopped) { Start-Service OneCBackupServer }
    foreach ($process in @($probe, $ftp)) {
        if ($process -and !$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    }
    Remove-Item -LiteralPath $lab -Recurse -Force
}
