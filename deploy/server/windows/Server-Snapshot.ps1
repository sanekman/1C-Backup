# Same-computer, same-build cold snapshots for the standard Windows installation.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('Backup','Verify','Restore')][string]$Action,
    [Parameter(Mandatory=$true)][string]$BackupDirectory,
    [string]$DataDirectory = (Join-Path $env:ProgramData 'OneCBackupServer'),
    [string]$InstallDirectory = (Split-Path $PSScriptRoot -Parent)
)
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSEdition -eq 'Desktop') { $env:PSModulePath=Join-Path $PSHOME 'Modules' }
Set-StrictMode -Version Latest
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if (!(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Запустите обслуживание сервера от имени администратора.' }
$DataDirectory=[IO.Path]::GetFullPath($DataDirectory).TrimEnd('\')
$InstallDirectory=[IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$BackupDirectory=[IO.Path]::GetFullPath($BackupDirectory).TrimEnd('\')
$server='OneCBackupServer'; $database='OneCBackupServerDatabase'
$manifestName='snapshot.json'
function Is-Within([string]$Path,[string]$Root) { return $Path.Equals($Root,[StringComparison]::OrdinalIgnoreCase) -or $Path.StartsWith($Root+'\',[StringComparison]::OrdinalIgnoreCase) }
function Plain-Path([string]$Path) {
    $current=$Path
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'В пути обнаружена ссылка или точка соединения. Для копии сервера используйте обычные папки.' }
        $parent=[IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }; $current=$parent
    }
}
function Local-Ntfs([string]$Path) {
    if ($Path -notmatch '^[a-zA-Z]:\\') { throw 'Выберите локальный диск NTFS. Сетевые папки этим способом пока не поддерживаются.' }
    $drive=[IO.Path]::GetPathRoot($Path).TrimEnd('\')
    # Win32_Volume lists real volumes, excluding SUBST aliases that can hide overlap.
    $volumes=@(Get-CimInstance Win32_Volume -Filter ("DriveLetter='"+$drive+"'"))
    if ($volumes.Count -ne 1 -or $volumes[0].FileSystem -ne 'NTFS' -or $volumes[0].DriveType -notin @(2,3)) { throw 'Нужен локальный том NTFS. Сетевые диски и виртуальные диски SUBST не поддерживаются.' }
    Plain-Path $Path
}
function Private-Directory([string]$Path,[bool]$ServiceAccess=$false) {
    [void][IO.Directory]::CreateDirectory($Path)
    $acl=New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true,$false); $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
    foreach ($sid in @('S-1-5-18','S-1-5-32-544') | Select-Object -Unique) {
        $rule=New-Object Security.AccessControl.FileSystemAccessRule -ArgumentList @((New-Object Security.Principal.SecurityIdentifier($sid)),'FullControl','ContainerInherit,ObjectInherit','None','Allow')
        $acl.AddAccessRule($rule)
    }
    if ($ServiceAccess) { $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule -ArgumentList @((New-Object Security.Principal.SecurityIdentifier('S-1-5-20')),'Modify','ContainerInherit,ObjectInherit','None','Allow'))) }
    Set-Acl -LiteralPath $Path -AclObject $acl
}
function Private-Backup([string]$Path) {
    $allowed=@('S-1-5-18','S-1-5-32-544')
    $acl=Get-Acl -LiteralPath $Path
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $allowed) { throw 'Владелец папки копии должен быть Администраторы или SYSTEM.' }
    foreach ($rule in $acl.Access) {
        if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -notin $allowed) { throw 'К папке копии имеют доступ другие пользователи. Копия содержит секреты: ограничьте доступ группой Администраторы и SYSTEM.' }
    }
}
function Tree([string]$Root) {
    Plain-Path $Root
    $pending=New-Object 'Collections.Generic.Stack[string]'; $pending.Push($Root)
    $visited=0
    while ($pending.Count) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            $visited++; if ($visited -gt 250000) { throw 'В копии больше 250000 файлов и папок; этот объём пока не поддерживается.' }
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'В данных найдена ссылка, точка соединения или внешнее табличное пространство. Нужна отдельная процедура копирования.' }
            if ($item.PSIsContainer) { $pending.Push($item.FullName) }
            $item
        }
    }
}
function Hash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Machine {
    $key=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64)
    try { $child=$key.OpenSubKey('SOFTWARE\Microsoft\Cryptography'); try { return [string]$child.GetValue('MachineGuid') } finally { $child.Dispose() } } finally { $key.Dispose() }
}
function Layout([string]$Root=$DataDirectory) {
    foreach ($name in @('server.json','connection-string.txt','server.pfx','certificate-password.txt','postgresql\PG_VERSION','postgresql\global\pg_control')) {
        if (!(Test-Path -LiteralPath (Join-Path $Root $name) -PathType Leaf)) { throw 'Не найдены обязательные файлы штатной Windows-установки сервера.' }
    }
    if ([IO.File]::ReadAllText((Join-Path $Root 'postgresql\PG_VERSION')).Trim() -ne '17') { throw 'Этот способ поддерживает только встроенную PostgreSQL 17.' }
    $connection=[IO.File]::ReadAllText((Join-Path $Root 'connection-string.txt')).Trim()
    if ($connection -cnotmatch '^Host=127\.0\.0\.1;Port=15433;Database=postgres;Username=onecbackup;Password=[A-Za-z0-9+/=]{32,}$') { throw 'Для внешней или изменённой настройки PostgreSQL нужна отдельная процедура копирования.' }
    $settings=Get-Content -LiteralPath (Join-Path $Root 'server.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($settings.Server.PSObject.Properties['DatabasePasswordFile'] -and $settings.Server.DatabasePasswordFile) { throw 'Внешний файл пароля PostgreSQL не входит в штатную копию. Нужна отдельная процедура.' }
    foreach ($pair in @(@('DataDirectory','data'),@('ConnectionStringFile','connection-string.txt'),@('HttpsCertificatePath','server.pfx'),@('HttpsCertificatePasswordFile','certificate-password.txt'))) {
        if ([IO.Path]::GetFullPath([string]$settings.Server.($pair[0])).TrimEnd('\') -ine (Join-Path $DataDirectory $pair[1])) { throw 'Данные или сертификат расположены вне штатной папки сервера. Нужна отдельная процедура копирования.' }
    }
    return $settings
}
function Build-Identity {
    $result=@{}
    foreach ($name in @('SPBackup.Server.exe','SPBackup.Server.dll','postgresql\bin\postgres.exe')) { $result[$name]=Hash (Join-Path $InstallDirectory $name) }
    return $result
}
function Owned-Services {
    $app=Get-CimInstance Win32_Service -Filter "Name='$server'"
    $pg=Get-CimInstance Win32_Service -Filter "Name='$database'"
    foreach ($svc in @($app,$pg)) {
        if (!$svc -or $svc.StartName -notin @('NT AUTHORITY\NetworkService','NT AUTHORITY\Network Service') -or $svc.State -notin @('Running','Stopped')) { throw 'Не найдены штатные службы NetworkService либо они сейчас запускаются или останавливаются. Дождитесь завершения и повторите.' }
    }
    $expected='"'+(Join-Path $InstallDirectory 'SPBackup.Server.exe')+'" --Server:ConfigFile "'+(Join-Path $DataDirectory 'server.json')+'"'
    if ($app.PathName -ine $expected) { throw 'Служба сервера использует другую папку программы или настройки. Обслуживание остановлено.' }
    $pgExe='"'+(Join-Path $InstallDirectory 'postgresql\bin\pg_ctl.exe')+'"'
    $pgCommand=$pg.PathName.Replace('/','\')
    $dataArgs=[regex]::Matches($pgCommand,'(?:^|\s)-D\s*(?:"([^"]+)"|(\S+))')
    if (!$pgCommand.StartsWith($pgExe,[StringComparison]::OrdinalIgnoreCase) -or $dataArgs.Count -ne 1 -or ([string]($dataArgs[0].Groups[1].Value+$dataArgs[0].Groups[2].Value)).TrimEnd('\') -ine (Join-Path $DataDirectory 'postgresql')) { throw 'Служба PostgreSQL указывает на другой кластер или программу. Обслуживание остановлено.' }
    return @{App=($app.State -eq 'Running'); Database=($pg.State -eq 'Running')}
}
function Stop-Both([bool]$RequireClean=$true) {
    foreach ($name in @($server,$database)) { $svc=Get-Service $name; if ($svc.Status -ne 'Stopped') { Stop-Service $name -Force; $svc.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(60)) } }
    if ($RequireClean -and (Test-Path -LiteralPath (Join-Path $DataDirectory 'postgresql\postmaster.pid'))) { throw 'PostgreSQL не завершила корректную остановку. Копия не отмечена готовой; рабочие данные не заменялись.' }
}
function Resume-Services($State) {
    foreach ($pair in @(@($server,$State.App),@($database,$State.Database))) {
        if (!$pair[1]) { $svc=Get-Service $pair[0]; if ($svc.Status -ne 'Stopped') { Stop-Service $pair[0] -Force; $svc.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(60)) } }
    }
    if ($State.Database) { Start-Service $database; (Get-Service $database).WaitForStatus('Running',[TimeSpan]::FromSeconds(60)) }
    if ($State.App) { Start-Service $server; (Get-Service $server).WaitForStatus('Running',[TimeSpan]::FromSeconds(60)) }
}
function Validate-RunningCluster {
    if ((Get-Service $database).Status -ne 'Running') { Start-Service $database; (Get-Service $database).WaitForStatus('Running',[TimeSpan]::FromSeconds(60)) }
    $oldPassword=$env:PGPASSWORD; $oldConnectTimeout=$env:PGCONNECT_TIMEOUT
    $env:PGCONNECT_TIMEOUT='5'
    try {
        $env:PGPASSWORD=[IO.File]::ReadAllText((Join-Path $DataDirectory 'connection-string.txt')).Trim().Substring('Host=127.0.0.1;Port=15433;Database=postgres;Username=onecbackup;Password='.Length)
        foreach ($setting in @('data_directory','config_file','hba_file','ident_file')) {
            $value=$null
            for ($attempt=0; $attempt -lt 30; $attempt++) {
                try { $value=& (Join-Path $InstallDirectory 'postgresql\bin\psql.exe') '-X' '-w' '-h' '127.0.0.1' '-p' '15433' '-U' 'onecbackup' '-d' 'postgres' '-At' '-c' ('SHOW '+$setting) 2>$null; $queryExit=$LASTEXITCODE } catch { $queryExit=1; $value=$null }
                if ($queryExit -eq 0) { break }; Start-Sleep -Seconds 1
            }
            if ($queryExit -ne 0 -or @($value).Count -ne 1) { throw 'Не удалось проверить расположение данных PostgreSQL. Рабочие данные не заменялись.' }
            $path=[IO.Path]::GetFullPath(([string]$value).Replace('/','\')).TrimEnd('\')
            if (!(Is-Within $path (Join-Path $DataDirectory 'postgresql'))) { throw 'Данные или файлы доступа PostgreSQL вынесены за пределы штатной папки. Нужна отдельная процедура.' }
            if ($setting -eq 'data_directory' -and $path -ine (Join-Path $DataDirectory 'postgresql')) { throw 'Работающая PostgreSQL использует другую папку данных. Копирование остановлено.' }
        }
    } finally { $env:PGPASSWORD=$oldPassword; $env:PGCONNECT_TIMEOUT=$oldConnectTimeout }
}
function Check-Stopped {
    foreach ($name in @($server,$database)) { if ((Get-Service $name).Status -ne 'Stopped') { throw 'Во время обслуживания запустилась служба. Копия не считается готовой.' } }
}
function Copy-Tree([string]$Source,[string]$Target) {
    $items=@(Tree $Source); $index=0
    $drive=New-Object IO.DriveInfo([IO.Path]::GetPathRoot($Target))
    $bytes=[long]0; $reserve=[long]1073741824
    foreach ($item in $items) {
        if (!$item.PSIsContainer) {
            if ($item.Length -gt [long]::MaxValue-$bytes-$reserve) { throw 'Объём копии превышает поддерживаемый предел.' }
            $bytes+=$item.Length
        }
    }
    if ($drive.AvailableFreeSpace -lt $bytes+$reserve) { throw 'Недостаточно места: нужен объём всей копии и ещё 1 ГиБ свободного пространства.' }
    foreach ($item in $items) {
        if ($index % 100 -eq 0) { Write-Progress -Activity 'SP Backup Server: копирование' -Status ("Файлов и папок: $index / "+$items.Count) -PercentComplete ([int](100*$index/[Math]::Max(1,$items.Count))) }
        $index++
        $relative=$item.FullName.Substring($Source.Length+1); $to=Join-Path $Target $relative
        if ($item.PSIsContainer) { [void][IO.Directory]::CreateDirectory($to) }
        else {
            if ($drive.AvailableFreeSpace -lt $item.Length+$reserve) { throw 'Свободное место уменьшилось. Копирование остановлено с резервом 1 ГиБ.' }
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($to)); [IO.File]::Copy($item.FullName,$to,$false)
        }
    }
    Write-Progress -Activity 'SP Backup Server: копирование' -Completed
}
function File-Records([string]$Root) {
    foreach ($item in @(Tree $Root)) { if (!$item.PSIsContainer) { [pscustomobject]@{Path=$item.FullName.Substring($Root.Length+1); Length=$item.Length; Sha256=(Hash $item.FullName)} } }
}
function Same-Records($Expected,$Actual) {
    if (@($Expected).Count -ne @($Actual).Count) { return $false }
    $map=@{}; foreach ($entry in $Actual) { if ($map.ContainsKey($entry.Path)) { return $false }; $map[$entry.Path]=$entry }
    foreach ($entry in $Expected) {
        if (!$map.ContainsKey($entry.Path) -or $map[$entry.Path].Length -ne $entry.Length -or $map[$entry.Path].Sha256 -cne $entry.Sha256) { return $false }
        $map.Remove($entry.Path)
    }
    return $map.Count -eq 0
}
function Directories([string]$Root) { foreach ($item in @(Tree $Root)) { if ($item.PSIsContainer) { $item.FullName.Substring($Root.Length+1) } } }
function Verify-Snapshot {
    Local-Ntfs $BackupDirectory; Private-Backup $BackupDirectory
    $manifest=Join-Path $BackupDirectory $manifestName
    if (!(Test-Path -LiteralPath $manifest -PathType Leaf) -or (Get-Item -LiteralPath $manifest).Length -gt 16777216) { throw 'Выберите готовую копию с файлом snapshot.json размером не более 16 МиБ.' }
    $m=Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($m.Format -ne 1 -or $m.Machine -ne (Machine) -or $m.DataDirectory -ine $DataDirectory -or $m.InstallDirectory -ine $InstallDirectory) { throw 'Копия создана на другом компьютере или для других путей установки. Здесь она не применяется.' }
    $build=Build-Identity
    foreach ($name in $build.Keys) { if ($m.Build.($name) -cne $build[$name]) { throw 'Нужен тот же комплект программы и PostgreSQL, с которым создана копия. Установите подходящие файлы программы перед восстановлением.' } }
    $payload=Join-Path $BackupDirectory 'data'; $actual=@(File-Records $payload); $expected=@($m.Files)
    if (!$expected.Count -or $expected.Count -gt 200000 -or $actual.Count -ne $expected.Count) { throw 'В копии не хватает файлов либо появились посторонние файлы.' }
    $map=@{}; foreach ($entry in $actual) { $map[$entry.Path]=$entry }
    foreach ($entry in $expected) {
        if ([string]::IsNullOrWhiteSpace($entry.Path) -or $entry.Path -match '(^[\\/]|:|(^|[\\/])\.\.?([\\/]|$))' -or $entry.Sha256 -cnotmatch '^[a-f0-9]{64}$') { throw 'В описании копии недопустимый путь или контрольная сумма.' }
        if (!$map.ContainsKey($entry.Path) -or $map[$entry.Path].Length -ne $entry.Length -or $map[$entry.Path].Sha256 -cne $entry.Sha256) { throw 'Файл копии отсутствует или изменён. Такая копия не применяется.' }
        $map.Remove($entry.Path)
    }
    if ($map.Count) { throw 'В описании копии повторяются файлы либо найдены неучтённые файлы.' }
    foreach ($required in @('server.json','connection-string.txt','server.pfx','certificate-password.txt','postgresql\PG_VERSION','postgresql\global\pg_control')) { if ($required -notin @($expected.Path)) { throw 'В копии отсутствуют обязательные данные сервера.' } }
    $actualDirectories=@(Directories $payload)
    if (@(Compare-Object @($m.Directories) $actualDirectories -CaseSensitive).Count) { throw 'В копии не хватает папок либо появились неучтённые папки.' }
    $null=Layout $payload
    return $m
}
function Ready($Settings) {
    [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
    $uri=([string]$Settings.Urls -replace '://[+*]:','://localhost:').TrimEnd('/')
    $prefix=if ($Settings.Server.PSObject.Properties['PathBase']) { [string]$Settings.Server.PathBase } else { '' }
    for ($attempt=0; $attempt -lt 30; $attempt++) {
        try { if ((Invoke-WebRequest -UseBasicParsing ($uri+$prefix+'/health/ready') -TimeoutSec 3).StatusCode -eq 200) { return } } catch { }
        Start-Sleep -Seconds 2
    }
    throw 'Не удалось подтвердить готовность восстановленного сервера по HTTPS. Выполняется возврат к предыдущим данным.'
}
Local-Ntfs $DataDirectory; Local-Ntfs $InstallDirectory; Local-Ntfs $BackupDirectory
if ((Is-Within $BackupDirectory $DataDirectory) -or (Is-Within $DataDirectory $BackupDirectory) -or (Is-Within $BackupDirectory $InstallDirectory) -or (Is-Within $InstallDirectory $BackupDirectory)) { throw 'Папка копии должна находиться отдельно от данных и программы сервера, без вложения друг в друга.' }
$locks=@(); $held=@()
try {
    foreach ($name in @('Global\SPBackupServerInstall','Global\SPBackupServerConfiguration')) {
        $created=$false
        $mutex=[Threading.Mutex]::new($false,$name,[ref]$created); $locks+=,$mutex
        if ($name -eq 'Global\SPBackupServerInstall' -and !$created) { throw 'Установка, создание или восстановление копии уже выполняется.' }
        try { $locked=$mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $mutex.ReleaseMutex(); throw 'Предыдущее обслуживание завершилось неожиданно. Проверьте службы и сохранённые папки перед повтором.' }
        if (!$locked) { throw 'Установка или обслуживание сервера уже выполняется.' }; $held+=,$mutex
    }
    if ($Action -eq 'Verify') { $verified=Verify-Snapshot; [pscustomobject]@{Action=$Action; Success=$true; Path=$BackupDirectory; Files=@($verified.Files).Count}; return }
    $state=Owned-Services
    if ($Action -eq 'Backup') {
        $settings=Layout
        if (Test-Path -LiteralPath $BackupDirectory) { throw 'Выберите новую папку копии. Существующие копии не перезаписываются.' }
        $before=Build-Identity; Private-Directory $BackupDirectory
        $complete=$false
        try {
            Validate-RunningCluster
            Stop-Both
            $payload=Join-Path $BackupDirectory 'data'; Private-Directory $payload
            Copy-Tree $DataDirectory $payload; Check-Stopped
            $records=@(File-Records $DataDirectory); $copied=@(File-Records $payload)
            if (!(Same-Records $records $copied)) { throw 'Содержимое копии не совпало с исходными файлами. Копия не отмечена готовой.' }
            $directories=@(Directories $DataDirectory)
            if (@(Compare-Object $directories @(Directories $payload) -CaseSensitive).Count) { throw 'Папки копии не совпали с исходными. Копия не отмечена готовой.' }
            Check-Stopped
            $after=Build-Identity; foreach ($name in $before.Keys) { if ($after[$name] -cne $before[$name]) { throw 'Во время копирования изменились файлы программы сервера. Копия не отмечена готовой.' } }
            $m=[ordered]@{Format=1; CreatedUtc=[DateTime]::UtcNow.ToString('o'); Machine=(Machine); DataDirectory=$DataDirectory; InstallDirectory=$InstallDirectory; Build=$before; Files=$records; Directories=$directories}
            $json=$m | ConvertTo-Json -Depth 6
            if ([Text.Encoding]::UTF8.GetByteCount($json) -gt 16777216) { throw 'Описание копии превысило поддерживаемый размер 16 МиБ.' }
            [IO.File]::WriteAllText((Join-Path $BackupDirectory ($manifestName+'.tmp')),$json,(New-Object Text.UTF8Encoding($false)))
            [IO.File]::Move((Join-Path $BackupDirectory ($manifestName+'.tmp')),(Join-Path $BackupDirectory $manifestName))
            $complete=$true
        } finally { Resume-Services $state }
        if ($complete) { [pscustomobject]@{Action=$Action; Success=$true; Path=$BackupDirectory; Files=$records.Count} }
    } else {
        $verified=Verify-Snapshot
        $stage=$DataDirectory+'.restore-'+[Guid]::NewGuid().ToString('N'); $previous=$DataDirectory+'.before-restore-'+[Guid]::NewGuid().ToString('N')
        Private-Directory $stage $true; Copy-Tree (Join-Path $BackupDirectory 'data') $stage
        # Validate the completed staged copy before touching live data or stopping services.
        $staged=@(File-Records $stage)
        if (!(Same-Records @($verified.Files) $staged)) { throw 'Подготовленные файлы восстановления не совпали с копией. Рабочие данные не заменялись.' }
        if (@(Compare-Object @($verified.Directories) @(Directories $stage) -CaseSensitive).Count) { throw 'Подготовленные папки восстановления не совпали с копией. Рабочие данные не заменялись.' }
        $hadPrevious=Test-Path -LiteralPath $DataDirectory
        $swapped=$false
        try {
            Stop-Both $false; Check-Stopped
            if ($hadPrevious) { [IO.Directory]::Move($DataDirectory,$previous) }
            try { [IO.Directory]::Move($stage,$DataDirectory); $swapped=$true } catch { if ($hadPrevious) { [IO.Directory]::Move($previous,$DataDirectory) }; throw }
            Resume-Services @{App=$true;Database=$true}; Ready (Layout)
        } catch {
            if ($swapped) {
                Stop-Both $false
                [IO.Directory]::Move($DataDirectory,$stage)
                if ($hadPrevious) { [IO.Directory]::Move($previous,$DataDirectory) }
            }
            Resume-Services $state
            throw
        }
        [pscustomobject]@{Action=$Action; Success=$true; Path=$BackupDirectory; PreviousData=$(if ($hadPrevious) { $previous } else { $null })}
    }
} finally {
    foreach ($mutex in $held) { $mutex.ReleaseMutex() }
    foreach ($mutex in $locks) { $mutex.Dispose() }
}
