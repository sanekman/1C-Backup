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
$certificate = $null; $serviceInstalled = $false; $provenance = $null
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
    $url = 'https://go.microsoft.com/fwlink/p/?linkid=2216019&clcid=0x409&culture=en-us&country=us'
    $bootstrap = Join-Path $private 'sql-bootstrap.exe'
    Invoke-WebRequest -Uri $url -OutFile $bootstrap
    Assert-MicrosoftSignature $bootstrap
    $media = Join-Path $private 'media'; New-Item -ItemType Directory -Path $media | Out-Null
    Invoke-OwnedTool $bootstrap @('/Action=Download','/MediaType=Core',('/MediaPath="' + $media + '"'),'/Quiet') 900
    $packages = @(Get-ChildItem -LiteralPath $media -Recurse -Filter 'SQLEXPR*.exe')
    if ($packages.Count -ne 1) { throw 'Expected exactly one full SQL Express engine installer.' }
    $package = $packages[0].FullName; Assert-MicrosoftSignature $package
    $provenance = @{ bootstrapUrl = $url; bootstrapSha256 = (Get-FileHash $bootstrap -Algorithm SHA256).Hash;
        mediaSha256 = (Get-FileHash $package -Algorithm SHA256).Hash; microsoftSignatureVerified = $true }
    $engine = Join-Path $private 'engine'
    Invoke-OwnedTool $package @(('/x:"' + $engine + '"'),'/q') 300
    $setup = Join-Path $engine 'SETUP.EXE'
    if (-not (Test-Path -LiteralPath $setup)) { throw 'Full SQL setup executable is missing.' }
    Invoke-OwnedTool $setup @('/Q','/ACTION=Install','/FEATURES=SQLENGINE',('/INSTANCENAME=' + $instance),
        '/SECURITYMODE=SQL',('/SAPWD=' + $password),('/SQLSYSADMINACCOUNTS="' + $owner.Name + '"'),
        '/SQLSVCSTARTUPTYPE=Automatic','/TCPENABLED=1','/NPENABLED=0','/UPDATEENABLED=False','/IACCEPTSQLSERVERLICENSETERMS') 1200
    $serviceInstalled = $true
    $instanceId = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL').$instance
    if (-not $instanceId) { throw 'The owned full SQL instance was not registered.' }
    $network = "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\$instanceId\MSSQLServer\SuperSocketNetLib"
    $certificate = New-SelfSignedCertificate -DnsName @($env:COMPUTERNAME,'localhost') -CertStoreLocation 'Cert:\LocalMachine\My' `
        -Provider 'Microsoft RSA SChannel Cryptographic Provider' -KeySpec KeyExchange -KeyLength 2048 -HashAlgorithm SHA256 `
        -Type SSLServerAuthentication -NotAfter (Get-Date).AddDays(2)
    $key = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
    try { $keyFile = Join-Path $env:ProgramData ('Microsoft\Crypto\RSA\MachineKeys\' + $key.CspKeyContainerInfo.UniqueKeyContainerName) }
    finally { $key.Dispose() }
    $serviceAccount = (Get-CimInstance Win32_Service -Filter "Name='$serviceName'").StartName
    $fileAcl = Get-Acl -LiteralPath $keyFile
    $fileAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule ($serviceAccount,'Read','Allow')))
    Set-Acl -LiteralPath $keyFile -AclObject $fileAcl
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
    Restart-Service -Name $serviceName
    $workspace = Join-Path $private 'fixture'; New-Item -ItemType Directory -Path $workspace | Out-Null
    & icacls.exe $workspace /grant "${serviceAccount}:(OI)(CI)(M)" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not grant the owned SQL service access to its fixture.' }
    # The service also needs traversal through this private parent, never its secrets.
    & icacls.exe $private /grant "${serviceAccount}:(RX)" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not grant SQL fixture traversal.' }
    Set-Content -LiteralPath (Join-Path $workspace 'owned-stand.marker') -Value $instance
    $binary = Join-Path $private 'probe'
    & dotnet publish (Join-Path $repo 'tests/WindowsSqlEngineSmoke') -c Release -r win-x64 --self-contained true -m:1 -nr:false -o $binary
    if ($LASTEXITCODE -ne 0) { throw 'Could not build the SQL engine probe.' }
    $env:SPBACKUP_NATIVE_SQL_ENGINE_TEST = '1'; $env:SPBACKUP_SQL_TEST_SERVER = "localhost,$port"; $env:SPBACKUP_SQL_TEST_PASSWORD = $password
    & (Join-Path $binary 'WindowsSqlEngineSmoke.exe') $workspace
    $probeExit = $LASTEXITCODE
    New-Item -ItemType Directory -Path $Output | Out-Null
    Copy-Item -LiteralPath (Join-Path $workspace 'report\sql-engine-result.json') -Destination $Output
    $provenance | ConvertTo-Json | Set-Content (Join-Path $Output 'sql-media-provenance.json') -Encoding UTF8
    if ($probeExit -ne 0) { throw 'Full SQL engine verification failed; see the sanitized result JSON.' }
} finally {
    Remove-Item Env:SPBACKUP_SQL_TEST_PASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:SPBACKUP_NATIVE_SQL_ENGINE_TEST -ErrorAction SilentlyContinue
    Remove-Item Env:SPBACKUP_SQL_TEST_SERVER -ErrorAction SilentlyContinue
    if ($serviceInstalled) { Stop-Service -Name $serviceName -ErrorAction Continue }
    if ($certificate) {
        Remove-Item -LiteralPath ('Cert:\LocalMachine\Root\' + $certificate.Thumbprint) -ErrorAction Continue
        Remove-Item -LiteralPath ('Cert:\LocalMachine\My\' + $certificate.Thumbprint) -DeleteKey -ErrorAction Continue
    }
    # Media, databases, installer logs and credentials are never uploaded.
    # The entire host is discarded by GitHub after this owned job.
}
