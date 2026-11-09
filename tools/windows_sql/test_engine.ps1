[CmdletBinding()]
param([Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:OS -ne 'Windows_NT') {
    throw 'This installer runs only on a disposable GitHub-hosted Windows runner.'
}
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$Output = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $Output) { throw 'Refusing to replace an existing test output directory.' }
$private = Join-Path $env:RUNNER_TEMP ('spb-sql-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $private | Out-Null
$instance = 'SPB' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$serviceName = 'MSSQL$' + $instance
$password = 'SpB!9-' + [Guid]::NewGuid().ToString('N')
Write-Host "::add-mask::$password"
$actorName = 'spbsql_' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$actorAccount = $env:COMPUTERNAME + '\' + $actorName
$actorPassword = 'SpBw!9-' + [Guid]::NewGuid().ToString('N')
Write-Host "::add-mask::$actorPassword"
$actorCreated = $false; $taskRegistered = $false
$taskName = 'SPBackupSQL-' + [Guid]::NewGuid().ToString('N')
$taskDiagnostics = $null
$certificate = $null; $serviceInstalled = $false; $provenance = $null
$hostsPath = Join-Path $env:WINDIR 'System32\drivers\etc\hosts'
$hostEntry = $null
$phase = 'download-bootstrap'
function Invoke-OwnedTool([string]$Executable, [string[]]$Arguments, [int]$TimeoutSeconds) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $private 'tool-output.txt') -RedirectStandardError (Join-Path $private 'tool-error.txt')
    try {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { $process.Kill(); throw 'Owned SQL setup operation timed out.' }
        if ($process.ExitCode -notin @(0,3010)) { throw "Owned SQL setup failed: exit $($process.ExitCode). Private setup logs remain on this disposable runner." }
    } finally { $process.Dispose() }
}
function Assert-MicrosoftSignature([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        throw 'SQL installation media does not have a valid Microsoft signature.'
    }
}
try {
    if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) { throw 'Refusing to replace an existing SQL service.' }
    $owner = [Security.Principal.WindowsIdentity]::GetCurrent()
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetOwner($owner.User); $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($owner.User.Value,'S-1-5-18','S-1-5-32-544') | Select-Object -Unique) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule ((New-Object Security.Principal.SecurityIdentifier $sid), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    Set-Acl -LiteralPath $private -AclObject $acl
    $actor = New-LocalUser -Name $actorName -Password (ConvertTo-SecureString $actorPassword -AsPlainText -Force) `
        -AccountNeverExpires -PasswordNeverExpires -Description 'Owned disposable SP Backup SQL stand'
    $actorCreated = $true
    try { Add-LocalGroupMember -SID (New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-545') -Member $actorAccount }
    catch { if ($_.FullyQualifiedErrorId -notlike 'MemberExists,*') { throw } }
    $url = 'https://go.microsoft.com/fwlink/p/?linkid=2216019&clcid=0x409&culture=en-us&country=us'
    $bootstrap = Join-Path $private 'sql-bootstrap.exe'
    Invoke-WebRequest -Uri $url -OutFile $bootstrap
    Assert-MicrosoftSignature $bootstrap
    $media = Join-Path $private 'media'; New-Item -ItemType Directory -Path $media | Out-Null
    $phase = 'download-full-engine'
    Invoke-OwnedTool $bootstrap @('/Action=Download','/MediaType=Core',('/MediaPath="' + $media + '"'),'/Quiet') 900
    $packages = @(Get-ChildItem -LiteralPath $media -Recurse -Filter 'SQLEXPR*.exe')
    if ($packages.Count -ne 1) { throw 'Expected exactly one full SQL Express engine installer.' }
    $package = $packages[0].FullName; Assert-MicrosoftSignature $package
    $provenance = @{ bootstrapUrl = $url; bootstrapSha256 = (Get-FileHash $bootstrap -Algorithm SHA256).Hash;
        mediaSha256 = (Get-FileHash $package -Algorithm SHA256).Hash; microsoftSignatureVerified = $true }
    $engine = Join-Path $private 'engine'
    $phase = 'extract-full-engine'
    Invoke-OwnedTool $package @(('/x:"' + $engine + '"'),'/q') 300
    $setup = Join-Path $engine 'SETUP.EXE'
    if (-not (Test-Path -LiteralPath $setup)) { throw 'Full SQL setup executable is missing.' }
    $phase = 'install-full-engine'
    Invoke-OwnedTool $setup @('/Q','/ACTION=Install','/FEATURES=SQLENGINE',('/INSTANCENAME=' + $instance),
        '/SECURITYMODE=SQL',('/SAPWD=' + $password),('/SQLSYSADMINACCOUNTS="' + $actorAccount + '"'),
        '/SQLSVCSTARTUPTYPE=Automatic','/TCPENABLED=1','/NPENABLED=0','/UPDATEENABLED=False','/IACCEPTSQLSERVERLICENSETERMS') 1200
    $serviceInstalled = $true
    $phase = 'configure-tls-and-loopback'
    $instanceId = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL').$instance
    if (-not $instanceId) { throw 'The owned full SQL instance was not registered.' }
    $network = "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\$instanceId\MSSQLServer\SuperSocketNetLib"
    $serviceAccount = (Get-CimInstance Win32_Service -Filter "Name='$serviceName'").StartName
    $serviceSid = (New-Object Security.Principal.NTAccount $serviceAccount).Translate([Security.Principal.SecurityIdentifier])
    # Apply the key ACL through the certificate provider itself. Physical key
    # filenames differ by provider and must never be inferred from RSA wrappers.
    # https://learn.microsoft.com/powershell/module/pki/new-selfsignedcertificate#-securitydescriptor
    $keySecurity = New-Object Security.AccessControl.FileSecurity
    $keySecurity.SetOwner($owner.User); $keySecurity.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($owner.User.Value,'S-1-5-18','S-1-5-32-544') | Select-Object -Unique) {
        $keySecurity.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule ((New-Object Security.Principal.SecurityIdentifier $sid),'FullControl','Allow')))
    }
    $keySecurity.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule ($serviceSid,'Read','Allow')))
    $certificate = New-SelfSignedCertificate -Subject ("CN=" + $env:COMPUTERNAME) -CertStoreLocation 'Cert:\LocalMachine\My' `
        -Provider 'Microsoft RSA SChannel Cryptographic Provider' -KeySpec KeyExchange -KeyLength 2048 -HashAlgorithm SHA256 `
        -TextExtension @("2.5.29.17={text}DNS=$env:COMPUTERNAME&DNS=localhost&IPAddress=127.0.0.1") `
        -SecurityDescriptor $keySecurity -Type SSLServerAuthentication -NotAfter (Get-Date).AddDays(2)
    if (-not $certificate.HasPrivateKey) { throw 'The owned SQL certificate has no private key.' }
    $public = Join-Path $private 'server.cer'; Export-Certificate -Cert $certificate -FilePath $public | Out-Null
    Import-Certificate -FilePath $public -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
    Set-ItemProperty $network Certificate $certificate.Thumbprint.ToLowerInvariant()
    Set-ItemProperty $network ForceEncryption 1
    $listener = New-Object Net.Sockets.TcpListener ([Net.IPAddress]::Loopback),0
    $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
    $tcp = Join-Path $network 'Tcp'; Set-ItemProperty $tcp Enabled 1; Set-ItemProperty $tcp ListenOnAllIPs 0
    $loopbackFound = $false
    foreach ($ip in Get-ChildItem -LiteralPath $tcp) {
        $settings = Get-ItemProperty -LiteralPath $ip.PSPath
        Set-ItemProperty -LiteralPath $ip.PSPath -Name TcpDynamicPorts -Value ''
        Set-ItemProperty -LiteralPath $ip.PSPath -Name TcpPort -Value ([string]$port)
        if ($ip.PSChildName -ne 'IPAll') {
            $enabled = [int]($settings.IpAddress -eq '127.0.0.1')
            if ($enabled) { $loopbackFound = $true }
            Set-ItemProperty -LiteralPath $ip.PSPath -Name Enabled -Value $enabled
        }
    }
    if (-not $loopbackFound) { throw 'No loopback SQL listener is available; public listeners are not enabled.' }
    $mismatchedHost = 'spb-' + [Guid]::NewGuid().ToString('N') + '.invalid'
    $hostEntry = "`r`n127.0.0.1 $mismatchedHost # SPBackup-owned-SQL-test`r`n"
    [IO.File]::AppendAllText($hostsPath, $hostEntry, (New-Object Text.UTF8Encoding $false))
    Restart-Service -Name $serviceName
    $listenDeadline = (Get-Date).AddSeconds(60)
    do {
        $socket = New-Object Net.Sockets.TcpClient
        try {
            $connect = $socket.ConnectAsync([Net.IPAddress]::Loopback, $port)
            $listening = $connect.Wait(1000) -and $socket.Connected
        } catch { $listening = $false }
        finally { $socket.Dispose() }
        if (-not $listening) {
            if ((Get-Date) -ge $listenDeadline) { throw 'The owned SQL engine did not open its configured loopback TCP port.' }
            Start-Sleep -Milliseconds 300
        }
    } while (-not $listening)
    $workspace = Join-Path $private 'fixture'; New-Item -ItemType Directory -Path $workspace | Out-Null
    & icacls.exe $workspace /grant "${serviceAccount}:(OI)(CI)(M)" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not grant the owned SQL service access to its fixture.' }
    # The service also needs traversal through this private parent, never its secrets.
    & icacls.exe $private /grant "${serviceAccount}:(RX)" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not grant SQL fixture traversal.' }
    Set-Content -LiteralPath (Join-Path $workspace 'owned-stand.marker') -Value $instance
    $binary = Join-Path $private 'probe'
    $phase = 'build-probe'
    & dotnet publish (Join-Path $repo 'tests/WindowsSqlEngineSmoke') -c Release -r win-x64 --self-contained true -m:1 -nr:false -o $binary
    if ($LASTEXITCODE -ne 0) { throw 'Could not build the SQL engine probe.' }
    & icacls.exe $private /grant ("*" + $actor.SID.Value + ':(RX)') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not grant the selected test user traversal.' }
    & icacls.exe $binary /grant ("*" + $actor.SID.Value + ':(OI)(CI)(RX)') /T /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not grant the selected user access to the probe.' }
    & icacls.exe $workspace /grant ("*" + $actor.SID.Value + ':(OI)(CI)(M)') /T /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not grant the selected user its workspace.' }
    $configPath = Join-Path $private 'task-config.json'
    $exitPath = Join-Path $workspace 'probe-exit.txt'
    $taskScript = Join-Path $private 'run-probe.ps1'
    @{ Executable = (Join-Path $binary 'WindowsSqlEngineSmoke.exe'); Workspace = $workspace;
        Environment = @{ SPBACKUP_NATIVE_SQL_ENGINE_TEST = '1'; SPBACKUP_SQL_TEST_SERVER = "tcp:127.0.0.1,$port";
            SPBACKUP_SQL_TEST_PASSWORD = $password; SPBACKUP_SQL_TEST_MISMATCH_SERVER = "tcp:$mismatchedHost,$port";
            SPBACKUP_SQL_TEST_INSTANCE = $instance; SPBACKUP_SQL_TEST_ACTOR_SID = $actor.SID.Value; GITHUB_SHA = $env:GITHUB_SHA } } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $configPath -Encoding UTF8
    @'
param([string]$ConfigPath,[string]$ExitPath)
$ErrorActionPreference='Stop'
$code=1
try {
    [IO.File]::WriteAllText(($ExitPath+'.started'),'started')
    $config=Get-Content -LiteralPath $ConfigPath -Raw|ConvertFrom-Json
    foreach($entry in $config.Environment.PSObject.Properties){[Environment]::SetEnvironmentVariable($entry.Name,[string]$entry.Value,'Process')}
    & $config.Executable $config.Workspace *> (Join-Path $config.Workspace 'probe-console.txt')
    $code=$LASTEXITCODE
} catch {
    @{errorType=$_.Exception.GetType().FullName;errorId=$_.FullyQualifiedErrorId}|ConvertTo-Json|
        Set-Content -LiteralPath ($ExitPath+'.error.json') -Encoding UTF8
} finally {
    [Environment]::SetEnvironmentVariable('SPBACKUP_SQL_TEST_PASSWORD',$null,'Process')
    [IO.File]::WriteAllText($ExitPath,[string]$code)
}
exit $code
'@ | Set-Content -LiteralPath $taskScript -Encoding UTF8
    foreach ($file in @($configPath,$taskScript)) {
        & icacls.exe $file /grant ("*" + $actor.SID.Value + ':(R)') | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not protect the selected-user task configuration.' }
    }
    $action = New-ScheduledTaskAction -Execute "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" `
        -WorkingDirectory $workspace `
        -Argument ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $taskScript + '" -ConfigPath "' + $configPath + '" -ExitPath "' + $exitPath + '"')
    $taskSettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 5) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName $taskName -Action $action -Settings $taskSettings -User $actorAccount -Password $actorPassword -RunLevel Limited | Out-Null
    $taskRegistered = $true
    $phase = 'verify-engine'
    $taskStartedAt = Get-Date
    Start-ScheduledTask -TaskName $taskName
    $taskDeadline = (Get-Date).AddSeconds(300)
    while (-not (Test-Path -LiteralPath $exitPath)) {
        $task = Get-ScheduledTask -TaskName $taskName
        $taskInfo = Get-ScheduledTaskInfo -TaskName $taskName
        $taskDiagnostics = @{ state = [string]$task.State; result = [long]$taskInfo.LastTaskResult;
            logonType = [string]$task.Principal.LogonType; started = (Test-Path -LiteralPath ($exitPath+'.started')) }
        if ($taskInfo.LastRunTime -ge $taskStartedAt.AddSeconds(-2) -and $task.State -notin @('Running','Queued') -and -not (Test-Path -LiteralPath $exitPath)) {
            throw ('The selected-user SQL task exited without a receipt: result 0x{0:X8}.' -f [long]$taskInfo.LastTaskResult)
        }
        if ((Get-Date) -ge $taskDeadline) { throw 'The selected-user SQL probe did not finish.' }
        Start-Sleep -Milliseconds 300
    }
    $probeExit = [int](Get-Content -LiteralPath $exitPath -Raw)
    if (Test-Path -LiteralPath ($exitPath+'.error.json')) {
        $taskDiagnostics = @{ result = $probeExit; started = (Test-Path -LiteralPath ($exitPath+'.started'));
            scriptError = (Get-Content -LiteralPath ($exitPath+'.error.json') -Raw | ConvertFrom-Json) }
        throw 'The selected-user task script failed before a SQL result was returned.'
    }
    New-Item -ItemType Directory -Path $Output | Out-Null
    Copy-Item -LiteralPath (Join-Path $workspace 'report\sql-engine-result.json') -Destination $Output
    $provenance | ConvertTo-Json | Set-Content (Join-Path $Output 'sql-media-provenance.json') -Encoding UTF8
    if ($probeExit -ne 0) { throw 'Full SQL engine verification failed; see the sanitized result JSON.' }
} catch {
    $setupError = $_
    $startupErrors = @()
    if ($phase -in @('configure-tls-and-loopback','verify-engine') -and $instanceId) {
        try {
            $parameters = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\$instanceId\MSSQLServer\Parameters"
            $errorArgument = @($parameters.PSObject.Properties | Where-Object { $_.Name -like 'SQLArg*' -and [string]$_.Value -like '-e*' } | Select-Object -First 1)
            if ($errorArgument.Count -eq 1) {
                $logPath = ([string]$errorArgument[0].Value).Substring(2)
                $startupErrors = @(Get-Content -LiteralPath $logPath -Tail 100 |
                    Where-Object { $_ -match '(?i)error|certificate|encryption|TDSSNI|TLS|SSL|failed|server is listening' } |
                    ForEach-Object { $_.Replace($password,'[redacted]') })
            }
        } catch { $startupErrors = @('SQL startup diagnostics were unavailable.') }
    }
    New-Item -ItemType Directory -Path $Output -Force | Out-Null
    if ($provenance) { $provenance | ConvertTo-Json | Set-Content (Join-Path $Output 'sql-media-provenance.json') -Encoding UTF8 }
    @{ success = $false; source = $env:GITHUB_SHA; phase = $phase;
        errorType = $setupError.Exception.GetType().FullName; error = $setupError.Exception.Message.Replace($password,'[redacted]');
        configuredEndpoint = "tcp:127.0.0.1,$port"; startupErrors = $startupErrors; task = $taskDiagnostics } |
        ConvertTo-Json -Depth 5 | Set-Content (Join-Path $Output 'sql-setup-result.json') -Encoding UTF8
    throw $setupError
} finally {
    if ($taskRegistered) {
        Stop-ScheduledTask -TaskName $taskName -ErrorAction Continue
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Continue
    }
    Remove-Item Env:SPBACKUP_SQL_TEST_PASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:SPBACKUP_NATIVE_SQL_ENGINE_TEST -ErrorAction SilentlyContinue
    Remove-Item Env:SPBACKUP_SQL_TEST_SERVER -ErrorAction SilentlyContinue
    Remove-Item Env:SPBACKUP_SQL_TEST_MISMATCH_SERVER -ErrorAction SilentlyContinue
    Remove-Item Env:SPBACKUP_SQL_TEST_INSTANCE -ErrorAction SilentlyContinue
    if ($hostEntry) {
        $hosts = [IO.File]::ReadAllText($hostsPath)
        if ($hosts.EndsWith($hostEntry, [StringComparison]::Ordinal)) {
            # Remove only our appended bytes; preserve the previous file exactly.
            $stream = [IO.File]::Open($hostsPath, 'Open', 'Write', 'Read')
            try { $stream.SetLength($stream.Length - [Text.Encoding]::UTF8.GetByteCount($hostEntry)) }
            finally { $stream.Dispose() }
        } else { Write-Warning 'The owned hosts entry was not removed because the disposable hosts file changed concurrently.' }
    }
    if ($serviceInstalled) { Stop-Service -Name $serviceName -ErrorAction Continue }
    if ($certificate) {
        Remove-Item -LiteralPath ('Cert:\LocalMachine\Root\' + $certificate.Thumbprint) -ErrorAction Continue
        Remove-Item -LiteralPath ('Cert:\LocalMachine\My\' + $certificate.Thumbprint) -DeleteKey -ErrorAction Continue
    }
    if ($actorCreated -and (Get-LocalUser -Name $actorName).SID.Value -eq $actor.SID.Value) { Remove-LocalUser -Name $actorName }
    # Media, databases, installer logs and credentials are never uploaded.
    # The entire host is discarded by GitHub after this owned job.
}
