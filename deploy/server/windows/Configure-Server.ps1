# Executed by the elevated installer. Never put passwords on a process command line.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$InstallDirectory,
    [string]$DataDirectory = (Join-Path $env:ProgramData 'OneCBackupServer'),
    [string]$ServerName = 'SP Backup Server',
    [string]$ServerAddress = ('https://'+$env:COMPUTERNAME+':7443/'),
    [string]$ServerNameFile,
    [string]$ServerAddressFile,
    [int]$HttpsPort = 0,
    [ValidateSet('Preserve','Allow','None')][string]$FirewallMode = 'Preserve',
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
if ($PSBoundParameters.ContainsKey('HttpsPort') -and ($HttpsPort -lt 1 -or $HttpsPort -gt 65535 -or $HttpsPort -eq 15433)) {
    throw 'HTTPS port must be between 1 and 65535 and differ from the internal database port 15433.'
}
if ($ServerNameFile) { $ServerName=[IO.File]::ReadAllText($ServerNameFile) }
if ($ServerAddressFile) { $ServerAddress=[IO.File]::ReadAllText($ServerAddressFile) }
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
        if ([string]::IsNullOrWhiteSpace($ServerName) -or $ServerName.Length -gt 128 -or $ServerName -match '[\x00-\x1f\x7f]') { throw 'Server display name must contain 1 to 128 characters.' }
        $address=$null
        if ($ServerAddress -match '[\s\\]' -or ![Uri]::TryCreate($ServerAddress,[UriKind]::Absolute,[ref]$address) -or
            $address.Scheme -ne 'https' -or $address.HostNameType -notin @([UriHostNameType]::Dns,[UriHostNameType]::IPv4,[UriHostNameType]::IPv6) -or
            $address.UserInfo -or $address.Query -or $address.Fragment -or $address.Port -lt 1 -or $address.Host -in @('0.0.0.0','[::]')) { throw 'Enter an absolute HTTPS server address without login, query or fragment.' }
        if ($HttpsPort -ne 0) { $builder=New-Object System.UriBuilder -ArgumentList $address; $builder.Port=$HttpsPort; $address=$builder.Uri }
        if ($address.Port -eq 15433) { throw 'HTTPS port 15433 is reserved for the internal database.' }
        $pathBase=$address.AbsolutePath.TrimEnd('/')
        if ($pathBase -and $pathBase -notmatch '^/[a-zA-Z0-9/_-]+$') { throw 'The address path may contain letters, digits, slash, hyphen and underscore.' }
        $listenPort=$address.Port
        $ServerAddress=$address.AbsoluteUri.TrimEnd('/')+'/'
        if (Test-Path $pgData) { throw 'Existing database found without configuration. Preserve it and recover server.json before reinstalling.' }
        if (!(Test-Path -LiteralPath $AdministratorPasswordFile -PathType Leaf)) { throw 'Administrator password file is required for first installation.' }
        $adminPassword = [IO.File]::ReadAllText($AdministratorPasswordFile).TrimEnd("`r","`n")
        if ($adminPassword.Length -lt 12 -or $adminPassword.Length -gt 256 -or $adminPassword -match '[\x00-\x1f]') { throw 'Administrator password must contain 12 to 256 characters.' }
        # Reserve fixed private PostgreSQL port; do not connect to or modify another installation.
        $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,15433)
        try { $listener.Start() } finally { $listener.Stop() }
        $httpsListener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Any,$listenPort)
        try { $httpsListener.Start() } finally { $httpsListener.Stop() }
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
        $names = @('localhost',$address.DnsSafeHost,$env:COMPUTERNAME) | Select-Object -Unique
        $san = foreach ($name in $names) {
            $ip=$null
            if ([Net.IPAddress]::TryParse($name,[ref]$ip)) { 'IPAddress='+$name } else { 'DNS='+$name }
        }
        $cert = New-SelfSignedCertificate -Type SSLServerAuthentication -Subject 'CN=localhost' -TextExtension ('2.5.29.17={text}'+($san -join '&')) -CertStoreLocation 'Cert:\LocalMachine\My' -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(5) -FriendlyName $ServerName
        Export-PfxCertificate -Cert $cert -FilePath (Join-Path $DataDirectory 'server.pfx') -Password (ConvertTo-SecureString $certificatePassword -AsPlainText -Force) | Out-Null
        Export-Certificate -Cert $cert -FilePath (Join-Path $DataDirectory 'server.cer') | Out-Null
        # Trust only on this machine. Remote agents use the explicit SHA-256 pin from the panel.
        Import-Certificate -FilePath (Join-Path $DataDirectory 'server.cer') -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
        Write-Private (Join-Path $DataDirectory 'certificate-thumbprint.txt') $cert.Thumbprint
        Remove-Item -LiteralPath ('Cert:\LocalMachine\My\'+$cert.Thumbprint)
        Write-Private (Join-Path $DataDirectory 'administrator-password.txt') $adminPassword
        $settings = @{
            AllowedHosts = ((@($names)+@($address.Host) | Select-Object -Unique) -join ';'); Urls = ('https://+:'+$listenPort)
            WindowsInstaller = @{ OpenFirewall = ($FirewallMode -ne 'None') }
            Server = @{
                DisplayName = $ServerName; PublicAddress = $ServerAddress; PathBase = $(if ($pathBase) { $pathBase } else { $null })
                DataDirectory = (Join-Path $DataDirectory 'data'); ConnectionStringFile = (Join-Path $DataDirectory 'connection-string.txt')
                HttpsCertificatePath = (Join-Path $DataDirectory 'server.pfx'); HttpsCertificatePasswordFile = (Join-Path $DataDirectory 'certificate-password.txt')
                BootstrapUser = 'admin'; BootstrapPasswordFile = (Join-Path $DataDirectory 'administrator-password.txt')
            }
        }
        Write-Private $config ($settings | ConvertTo-Json -Depth 5)
    }
    # Older installations have only panel-url.txt; preserve their address, database and certificate.
    $configuration = Get-Content -LiteralPath $config -Raw -Encoding UTF8 | ConvertFrom-Json
    $migrated=$false
    if (!$configuration.Server.PSObject.Properties['DisplayName']) {
        $configuration.Server | Add-Member -NotePropertyName DisplayName -NotePropertyValue $ServerName
        $migrated=$true
    }
    if (!$configuration.Server.PSObject.Properties['PublicAddress']) {
        $oldAddress=Join-Path $DataDirectory 'panel-url.txt'
        $publicAddress=if (Test-Path $oldAddress) { [IO.File]::ReadAllText($oldAddress).Trim() } else { $ServerAddress }
        $configuration.Server | Add-Member -NotePropertyName PublicAddress -NotePropertyValue $publicAddress
        $migrated=$true
    }
    if ($migrated) { Write-Private $config ($configuration | ConvertTo-Json -Depth 5) }
    Write-Private (Join-Path $DataDirectory 'panel-url.txt') $configuration.Server.PublicAddress
    $listenAddress=[Uri]($configuration.Urls -replace '://[+*]:','://localhost:')
    $listenPort=$listenAddress.Port
    $pathBase=if ($configuration.Server.PSObject.Properties['PathBase']) { $configuration.Server.PathBase } else { '' }
    $readyAddress='https://localhost:'+$listenPort+$pathBase+'/health/ready'
    if (!(Get-Service -Name $pgService -ErrorAction SilentlyContinue)) {
        Run-Native (Join-Path $pg 'pg_ctl.exe') @('register','-N',$pgService,'-D',$pgData,'-U','NT AUTHORITY\NetworkService','-S','auto')

    }
    Step 'database-service'
    Start-Service -Name $pgService
    (Get-Service $pgService).WaitForStatus('Running',[TimeSpan]::FromSeconds(60))
    $exe = Join-Path $InstallDirectory 'SPBackup.Server.exe'
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
        New-Service -Name $serverService -DisplayName 'SP Backup Server' -BinaryPathName ('"'+$exe+'" --Server:ConfigFile "'+$config+'"') -StartupType Automatic -DependsOn $pgService -Credential (New-Object Management.Automation.PSCredential('NT AUTHORITY\NetworkService',(New-Object Security.SecureString))) | Out-Null

    }
    Run-Native 'sc.exe' @('failure',$serverService,'reset=','86400','actions=','restart/10000/restart/30000/restart/60000')
    Run-Native 'sc.exe' @('failure',$pgService,'reset=','86400','actions=','restart/10000/restart/30000/restart/60000')
    Step 'firewall'
    $rule = Get-NetFirewallRule -Name 'OneCBackupServer-Https' -ErrorAction SilentlyContinue
    if ($FirewallMode -eq 'Allow') { $openFirewall=$true }
    elseif ($FirewallMode -eq 'None') { $openFirewall=$false }
    elseif ($configuration.PSObject.Properties['WindowsInstaller'] -and $configuration.WindowsInstaller -and $configuration.WindowsInstaller.PSObject.Properties['OpenFirewall']) {
        if ($configuration.WindowsInstaller.OpenFirewall -isnot [bool]) { throw 'Invalid saved firewall preference.' }
        $openFirewall=$configuration.WindowsInstaller.OpenFirewall
    } else {
        # Legacy upgrades respect a removed/disabled rule instead of reopening access silently.
        $openFirewall=[bool]($rule | Where-Object { $_.Enabled -eq 'True' })
    }
    if ($openFirewall) {
        if ($rule) {
            $rule | Set-NetFirewallRule -Enabled True -Direction Inbound -Action Allow -Profile Domain,Private -Program $exe
            $rule | Get-NetFirewallPortFilter | Set-NetFirewallPortFilter -Protocol TCP -LocalPort $listenPort
        } else {
            New-NetFirewallRule -Name 'OneCBackupServer-Https' -DisplayName 'SP Backup Server HTTPS' -Direction Inbound -Protocol TCP -LocalPort $listenPort -Program $exe -Profile Domain,Private -Action Allow | Out-Null
        }
    } elseif ($rule) { $rule | Remove-NetFirewallRule }
    if (!$configuration.PSObject.Properties['WindowsInstaller'] -or !$configuration.WindowsInstaller) {
        $configuration | Add-Member -NotePropertyName WindowsInstaller -NotePropertyValue ([pscustomobject]@{OpenFirewall=[bool]$openFirewall}) -Force
    } else { $configuration.WindowsInstaller | Add-Member -NotePropertyName OpenFirewall -NotePropertyValue ([bool]$openFirewall) -Force }
    Write-Private $config ($configuration | ConvertTo-Json -Depth 5)
    Step 'server-service'
    Start-Service -Name $serverService
    (Get-Service $serverService).WaitForStatus('Running',[TimeSpan]::FromSeconds(60))
    # Positive HTTPS readiness using OS trust; TLS validation is never disabled.
    $ready = $false
    for ($attempt=0; $attempt -lt 30; $attempt++) {
        try { $response=Invoke-WebRequest -UseBasicParsing -Uri $readyAddress -TimeoutSec 3; if ($response.StatusCode -eq 200) { $ready=$true; break } } catch { }
        Start-Sleep -Seconds 2
    }
    if (!$ready) { throw 'The Windows services were installed but HTTPS readiness failed. See the server logs.' }
    Step 'ready'
    Write-Output 'SP Backup Server installed. Open the panel from the Start menu and sign in as admin.'
} catch {
    # Keep all persistent files. A failed setup must never delete a database or reset a password.
    Add-Content -LiteralPath (Join-Path $DataDirectory 'setup.log') -Encoding UTF8 -Value ('failed '+$stage+' '+$_.Exception.GetType().Name)
    throw
} finally {
    # The supplied one-time password is no longer needed; installer also removes its temporary file.
    $adminPassword=$null; $databasePassword=$null; $certificatePassword=$null
}
