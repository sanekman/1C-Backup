# Executed by the elevated installer. Never put passwords on a process command line.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$InstallDirectory,
    [string]$DataDirectory = (Join-Path $env:ProgramData 'OneCBackupServer'),
    [string]$ServerName = $env:COMPUTERNAME,
    [string]$AdministratorPasswordFile
)
$ErrorActionPreference = 'Stop'
# A parent PowerShell 7 process may pass incompatible module paths to Windows PowerShell 5.1.
if ($PSVersionTable.PSEdition -eq 'Desktop') { $env:PSModulePath=Join-Path $PSHOME 'Modules' }
Set-StrictMode -Version Latest
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
function Run-Native([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments *> $null
    if ($LASTEXITCODE -ne 0) { throw ('Command failed: ' + [IO.Path]::GetFileName($Executable)) }
}
function Write-Private([string]$Path, [string]$Value) { [IO.File]::WriteAllText($Path,$Value,(New-Object Text.UTF8Encoding($false))) }
function Random-Password {
    $bytes = New-Object byte[] 48
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes); return [Convert]::ToBase64String($bytes) } finally { $rng.Dispose() }
}
if ($ServerName -notmatch '^[a-zA-Z0-9][a-zA-Z0-9.-]{0,252}$') { throw 'Invalid server DNS name.' }
$InstallDirectory = [IO.Path]::GetFullPath($InstallDirectory)
$DataDirectory = [IO.Path]::GetFullPath($DataDirectory)
$config = Join-Path $DataDirectory 'server.json'
$pg = Join-Path $InstallDirectory 'postgresql\bin'
$pgData = Join-Path $DataDirectory 'postgresql'
$pgService = 'OneCBackupServerDatabase'
$serverService = 'OneCBackupServer'
$owner = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
# SID based ACLs work on every Windows language. Explicit owner also allows initdb's restricted token.
Run-Native 'icacls.exe' @($DataDirectory,'/inheritance:r','/grant:r',"*${owner}:(OI)(CI)F",'*S-1-5-18:(OI)(CI)F','*S-1-5-32-544:(OI)(CI)F','*S-1-5-20:(OI)(CI)M')
foreach ($name in @($serverService,$pgService)) {
    $service = Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') { Stop-Service -Name $name -Force; $service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(60)) }
}
$stage = 'configuration'
function Step([string]$Name) { $script:stage=$Name; Add-Content -LiteralPath (Join-Path $DataDirectory 'setup.log') -Encoding UTF8 -Value ((Get-Date).ToUniversalTime().ToString('o')+' '+$Name) }
try {
    if (!(Test-Path $config)) {
        if (Test-Path $pgData) { throw 'Existing database found without configuration. Preserve it and recover server.json before reinstalling.' }
        if (!(Test-Path -LiteralPath $AdministratorPasswordFile -PathType Leaf)) { throw 'Administrator password file is required for first installation.' }
        $adminPassword = [IO.File]::ReadAllText($AdministratorPasswordFile).TrimEnd("`r","`n")
        if ($adminPassword.Length -lt 12 -or $adminPassword.Length -gt 256 -or $adminPassword -match '[\x00-\x1f]') { throw 'Administrator password must contain 12 to 256 characters.' }
        # Reserve fixed private PostgreSQL port; do not connect to or modify another installation.
        $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,15433)
        try { $listener.Start() } finally { $listener.Stop() }
        $databasePassword = Random-Password
        $dbPasswordPath = Join-Path $DataDirectory 'database-password.txt'
        Write-Private $dbPasswordPath $databasePassword
        Step 'database-initialization'
        Run-Native (Join-Path $pg 'initdb.exe') @('-D',$pgData,'--username=onecbackup','--auth=scram-sha-256','--encoding=UTF8','--locale=C',('--pwfile='+$dbPasswordPath))
        Add-Content -LiteralPath (Join-Path $pgData 'postgresql.conf') -Encoding ASCII -Value "`nlisten_addresses = '127.0.0.1'`nport = 15433`npassword_encryption = 'scram-sha-256'`n"
        Remove-Item -LiteralPath $dbPasswordPath
        Write-Private (Join-Path $DataDirectory 'connection-string.txt') ("Host=127.0.0.1;Port=15433;Database=postgres;Username=onecbackup;Password="+$databasePassword)
        Step 'https-certificate'
        $certificatePassword = Random-Password
        Write-Private (Join-Path $DataDirectory 'certificate-password.txt') $certificatePassword
        $names = @('localhost',$ServerName,$env:COMPUTERNAME) | Select-Object -Unique
        $cert = New-SelfSignedCertificate -DnsName $names -CertStoreLocation 'Cert:\LocalMachine\My' -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(5) -FriendlyName '1C-Backup Server'
        Export-PfxCertificate -Cert $cert -FilePath (Join-Path $DataDirectory 'server.pfx') -Password (ConvertTo-SecureString $certificatePassword -AsPlainText -Force) | Out-Null
        Export-Certificate -Cert $cert -FilePath (Join-Path $DataDirectory 'server.cer') | Out-Null
        # Trust only on this machine. Remote agents use the explicit SHA-256 pin from the panel.
        Import-Certificate -FilePath (Join-Path $DataDirectory 'server.cer') -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
        Write-Private (Join-Path $DataDirectory 'certificate-thumbprint.txt') $cert.Thumbprint
        Remove-Item -LiteralPath ('Cert:\LocalMachine\My\'+$cert.Thumbprint)
        Write-Private (Join-Path $DataDirectory 'administrator-password.txt') $adminPassword
        $settings = @{
            AllowedHosts = ($names -join ';'); Urls = 'https://0.0.0.0:7443'
            Server = @{
                DataDirectory = (Join-Path $DataDirectory 'data'); ConnectionStringFile = (Join-Path $DataDirectory 'connection-string.txt')
                HttpsCertificatePath = (Join-Path $DataDirectory 'server.pfx'); HttpsCertificatePasswordFile = (Join-Path $DataDirectory 'certificate-password.txt')
                BootstrapUser = 'admin'; BootstrapPasswordFile = (Join-Path $DataDirectory 'administrator-password.txt')
            }
        }
        Write-Private $config ($settings | ConvertTo-Json -Depth 5)
        Write-Private (Join-Path $DataDirectory 'panel-url.txt') ("https://"+$ServerName+':7443/')
    }
    if (!(Get-Service -Name $pgService -ErrorAction SilentlyContinue)) {
        Run-Native (Join-Path $pg 'pg_ctl.exe') @('register','-N',$pgService,'-D',$pgData,'-U','NT AUTHORITY\NetworkService','-S','auto')

    }
    Step 'database-service'
    Start-Service -Name $pgService
    (Get-Service $pgService).WaitForStatus('Running',[TimeSpan]::FromSeconds(60))
    $exe = Join-Path $InstallDirectory 'OneCBackup.Server.exe'
    # pg_ctl service readiness precedes database readiness; use bounded --initialize/--migrate retries.
    $adminFile = Join-Path $DataDirectory 'administrator-password.txt'
    $mode = if (Test-Path $adminFile) { '--provision' } else { '--migrate' }
    Step 'schema-and-administrator'
    $initialized = $false
    for ($attempt=0; $attempt -lt 30; $attempt++) {
        & $exe '--Server:ConfigFile' $config $mode *> $null
        if ($LASTEXITCODE -eq 0) { $initialized=$true; break }
        Start-Sleep -Seconds 2
    }
    if (!$initialized) { throw 'Database initialization failed. Configuration and database have been retained for recovery.' }
    Remove-Item -LiteralPath $adminFile -ErrorAction SilentlyContinue
    if (!(Get-Service -Name $serverService -ErrorAction SilentlyContinue)) {
        New-Service -Name $serverService -DisplayName '1C-Backup Server' -BinaryPathName ('"'+$exe+'" --Server:ConfigFile "'+$config+'"') -StartupType Automatic -DependsOn $pgService -Credential (New-Object Management.Automation.PSCredential('NT AUTHORITY\NetworkService',(New-Object Security.SecureString))) | Out-Null

    }
    Run-Native 'sc.exe' @('failure',$serverService,'reset=','86400','actions=','restart/10000/restart/30000/restart/60000')
    Run-Native 'sc.exe' @('failure',$pgService,'reset=','86400','actions=','restart/10000/restart/30000/restart/60000')
    if (!(Get-NetFirewallRule -Name 'OneCBackupServer-Https' -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -Name 'OneCBackupServer-Https' -DisplayName '1C-Backup Server HTTPS' -Direction Inbound -Protocol TCP -LocalPort 7443 -Program $exe -Profile Domain,Private -Action Allow | Out-Null
    }
    Step 'server-service'
    Start-Service -Name $serverService
    (Get-Service $serverService).WaitForStatus('Running',[TimeSpan]::FromSeconds(60))
    # Positive HTTPS readiness using OS trust; TLS validation is never disabled.
    $ready = $false
    for ($attempt=0; $attempt -lt 30; $attempt++) {
        try { $response=Invoke-WebRequest -UseBasicParsing -Uri 'https://localhost:7443/health/ready' -TimeoutSec 3; if ($response.StatusCode -eq 200) { $ready=$true; break } } catch { }
        Start-Sleep -Seconds 2
    }
    if (!$ready) { throw 'The Windows services were installed but HTTPS readiness failed. See the server logs.' }
    Step 'ready'
    Write-Output '1C-Backup Server installed. Open the panel from the Start menu and sign in as admin.'
} catch {
    # Keep all persistent files. A failed setup must never delete a database or reset a password.
    Add-Content -LiteralPath (Join-Path $DataDirectory 'setup.log') -Encoding UTF8 -Value ('failed '+$stage+' '+$_.Exception.GetType().Name)
    throw
} finally {
    # The supplied one-time password is no longer needed; installer also removes its temporary file.
    $adminPassword=$null; $databasePassword=$null; $certificatePassword=$null
}
