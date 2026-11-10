[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourcePath,
    [Parameter(Mandatory)][string]$PlatformVersion,
    [string]$PlatformExecutable,
    [string]$AgentExecutable,
    [string]$WorkParent = (Join-Path $env:LOCALAPPDATA 'SPBackup-Tests'),
    [switch]$PlatformOnly,
    [switch]$AskOneCLogin,
    [ValidateRange(1,10080)][int]$DumpTimeoutMinutes = 120
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Этот сценарий запускается только в Windows.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$utf8 = New-Object Text.UTF8Encoding $false
$root = $null; $shareName = $null; $ownsRoot = $false
$ownedPlatforms = New-Object 'Collections.Generic.List[Diagnostics.Process]'
$checks = New-Object 'Collections.Generic.List[object]'
$oneCCredentialKey = $null; $oneCUsername = $null; $oneCPassword = $null; $login = $null
$ownedLoginFiles = New-Object 'Collections.Generic.List[string]'
$report = [ordered]@{
    format = 1; scenario = 'real-windows-onec'; startedUtc = [DateTime]::UtcNow.ToString('o')
    success = $false; platformVersion = ''; agentVersion = ''; actor = 'current-interactive-user'
    checks = $checks; errorType = $null; errorCode = $null; phase = 'preflight'; diskSpace = $null
    scope = $(if ($PlatformOnly) { 'platform-only' } else { 'all' }); selectedChecksPassed = $false
    authentication = $(if ($AskOneCLogin) { 'infobase-user' } else { 'no-user-configured' })
}
function Write-Utf8([string]$Path, [string]$Text) { [IO.File]::WriteAllText($Path, $Text, $utf8) }
function Add-Check([string]$Name, [string]$Status, [string]$Note) {
    $checks.Add([ordered]@{ name = $Name; status = $Status; note = $Note })
    Write-Host "[$Status] $Note"
}
function Start-Owned([string]$Executable, [string[]]$Arguments, [bool]$Redirect = $false) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Executable
    $start.Arguments = ($Arguments | ForEach-Object {
        if ($_.Contains('"')) { throw 'Аргумент содержит недопустимую кавычку.' }
        '"' + $_.TrimEnd('\') + '"'
    }) -join ' '
    $start.UseShellExecute = $false; $start.WorkingDirectory = $root
    $start.RedirectStandardOutput = $Redirect; $start.RedirectStandardError = $Redirect
    return [Diagnostics.Process]::Start($start)
}
function Wait-Closed([string]$Directory) {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        try {
            $lease = New-Object IO.FileStream (Join-Path $Directory '1Cv8.1CD'), 'Open', 'Read', 'None'
            $lease.Dispose(); return
        } catch [IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) { throw 'Тестовая база ещё открыта. Закройте только её окно.' }
            Start-Sleep -Milliseconds 200
        }
    } while ($true)
}
function Start-AuthenticatedPlatform([string[]]$Arguments) {
    $parameters = @($Arguments) + @('/WA-')
    if (-not $oneCCredentialKey) { return Start-Owned $PlatformExecutable $parameters }
    $parameters += @('/N',$oneCUsername,'/P',$oneCPassword)
    $file = Join-Path $root ('login-' + [Guid]::NewGuid().ToString('N') + '.prm')
    $encoding = [Text.Encoding]::GetEncoding([Text.Encoding]::Default.CodePage, [Text.EncoderFallback]::ExceptionFallback, [Text.DecoderFallback]::ExceptionFallback)
    $parts = New-Object 'Collections.Generic.List[string]'; $valueExpected = $false
    foreach($parameter in $parameters) {
        if($valueExpected) { $parts.Add('"' + $parameter.Replace('"','""') + '"'); $valueExpected=$false }
        else { $parts.Add($parameter); $valueExpected=$parameter -in @('/F','/DumpIB','/RestoreIB','/Out','/N','/P') }
    }
    $text = $parts -join ' '
    [IO.File]::WriteAllText($file,$text,$encoding)
    $ownedLoginFiles.Add($file)
    return Start-Owned $PlatformExecutable @('/@',$file)
}
function New-Job([string]$Source, [string]$Destination, [bool]$AllowOpen, [bool]$Dump = $false) {
    $settings = @{ ExecutablePath = $PlatformExecutable; AutoSelectPlatform = $false; TimeoutMinutes = $DumpTimeoutMinutes }
    if ($Dump -and $oneCCredentialKey) { $settings.Username = $oneCUsername; $settings.CredentialKey = $oneCCredentialKey }
    return [ordered]@{
        Id = [Guid]::NewGuid().ToString(); Name = 'SP Backup acceptance'
        Kind = 'OneCFile'; Source = $Source; DestinationDirectory = $Destination
        StorageKind = $(if ($Destination.StartsWith('\\')) { 'NetworkShare' } else { 'LocalDirectory' })
        KeepLast = 20; Enabled = $false; ScheduleKind = 'Manual'
        OneCMethod = $(if ($Dump) { 'PlatformDump' } else { 'FileArchive' })
        AllowOpenOneCDatabase = $AllowOpen
        OneC = $settings
    }
}
function Invoke-Job($Job, [string]$Case, [bool]$CancelPlatform = $false) {
    $caseProfile = Join-Path $root ('profiles\' + $Case)
    New-Item -ItemType Directory -Path $caseProfile | Out-Null
    Write-Utf8 (Join-Path $caseProfile 'jobs.json') (ConvertTo-Json -InputObject @($Job) -Depth 10)
    Write-Host "Проверка $Case. Если появится окно входа 1С, заполните его только на этом ПК."
    $process = Start-Owned $AgentExecutable @('--run-job', $Job.Id, '--data-dir', $caseProfile, '--quiet', '--poll-seconds', '1') $true
    $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
    $deadline = [DateTime]::UtcNow.AddMinutes($DumpTimeoutMinutes + 2)
    $cancelSent = $false; $cancelledChild = $null
    try {
        while (-not $process.WaitForExit(200)) {
            if ([DateTime]::UtcNow -ge $deadline) { throw 'Истёк срок проверки задания.' }
            if ($CancelPlatform -and -not $cancelSent) {
                $child = Get-CimInstance Win32_Process -Filter "ParentProcessId = $($process.Id)" -OperationTimeoutSec 3 |
                    Where-Object { $_.ExecutablePath -eq $PlatformExecutable -and $_.CommandLine -and
                        (($_.CommandLine.Contains('/DumpIB') -and $_.CommandLine.Contains($root)) -or
                         ($oneCCredentialKey -and $_.CommandLine.Contains('/@') -and $_.CommandLine.Contains('OneCBackup-ib-login-'))) } |
                    Select-Object -First 1
                if ($child) {
                    $cancelledChild = $child
                    $stop = Start-Owned $AgentExecutable @('--stop', '--data-dir', $caseProfile) $true
                    $stopOutput = $stop.StandardOutput.ReadToEndAsync(); $stopError = $stop.StandardError.ReadToEndAsync()
                    if (-not $stop.WaitForExit(30000)) { $stop.Kill(); throw 'Команда остановки тестового задания не завершилась.' }
                    $stopOutput.GetAwaiter().GetResult() | Out-Null
                    $stopError.GetAwaiter().GetResult() | Out-Null
                    $cancelSent = $stop.ExitCode -eq 0; $stop.Dispose()
                }
            }
        }
        if ($cancelSent -and $cancelledChild) {
            $remaining = Get-CimInstance Win32_Process -Filter "ProcessId = $($cancelledChild.ProcessId)" -OperationTimeoutSec 3
            if ($remaining -and $remaining.CreationDate -eq $cancelledChild.CreationDate) {
                throw 'Платформа осталась работающей после отмены тестового задания.'
            }
        }
        Write-Utf8 (Join-Path $caseProfile 'console.txt') ($stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult())
        $run = @(Get-Content (Join-Path $caseProfile 'runs.json') -Raw | ConvertFrom-Json) |
            Where-Object { $_.JobId -eq $Job.Id } | Select-Object -Last 1
        if (-not $run) { throw 'Агент не сохранил итог задания. Подробности остались в тестовом каталоге.' }
        return [pscustomobject]@{ Run = $run; CancelSent = $cancelSent }
    } finally {
        # Only this newly created agent is stopped; its Windows job object owns its children.
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(30000) | Out-Null }
        $process.Dispose()
    }
}
function Is-Succeeded($Run) { return [string]$Run.Status -in @('Succeeded', '1') }
function Has-OpenWarning($Run) {
    return @($Run.Warnings | Where-Object { $_ -like '*копирование открытой базы*' }).Count -gt 0
}
function Assert-Archive($Run, [string]$ExpectedDatabase, [string]$Case) {
    if (-not (Is-Succeeded $Run) -or -not $Run.Backup) { throw "Не создана успешная тестовая копия: $Case." }
    $archivePath = [string]$Run.Backup.Location
    if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $Run.Backup.Sha256) { throw 'SHA-256 архива не совпадает с каталогом.' }
    $destination = Join-Path $root ('restored\' + $Case)
    New-Item -ItemType Directory -Path $destination | Out-Null
    $zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        if ($zip.Entries.Count -ne 1 -or $zip.Entries[0].FullName -ine '1Cv8.1CD') { throw 'Неожиданный состав ZIP.' }
        $file = Join-Path $destination '1Cv8.1CD'
        [IO.Compression.ZipFileExtensions]::ExtractToFile($zip.Entries[0], $file, $false)
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $ExpectedDatabase -Algorithm SHA256).Hash) { throw 'Восстановленные байты отличаются от закрытого тестового источника.' }
    } finally { $zip.Dispose() }
}
function Test-FileCases([string]$Source, [string]$Destination, [string]$Tag) {
    Wait-Closed $Source
    foreach ($allow in @($false, $true)) {
        $case = $Tag + '-closed-' + $allow.ToString().ToLowerInvariant()
        $result = Invoke-Job (New-Job $Source $Destination $allow) $case
        Assert-Archive $result.Run (Join-Path $Source '1Cv8.1CD') $case
        if (Has-OpenWarning $result.Run) { throw 'Закрытая база ошибочно получила предупреждение об открытой базе.' }
        Add-Check $case 'passed' 'Закрытая настоящая база: ZIP восстановлен побайтно, лишнего предупреждения нет.'
    }
    Write-Host 'Откроется отдельная ТЕСТОВАЯ база. Дождитесь загрузки. Менять данные не требуется.'
    Write-Host "Путь тестовой базы: $Source"
    Write-Host 'Если появился список баз, выберите тестовую базу с этим путём и нажмите «1С:Предприятие». Сам список баз ещё не является открытым сеансом.'
    $platform = Start-Owned $PlatformExecutable @('ENTERPRISE', '/F', $Source, '/DisableStartupMessages')
    $ownedPlatforms.Add($platform)
    Read-Host 'Только после открытия данных ТЕСТОВОЙ базы нажмите Enter' | Out-Null
    $busy = $false
    try {
        $lease = New-Object IO.FileStream (Join-Path $Source '1Cv8.1CD'), 'Open', 'Read', 'None'
        $lease.Dispose()
    } catch [IO.IOException] { $busy = $true }
    if (-not $busy) {
        Add-Check ($Tag + '-open-session') 'not-exercised' 'Платформа не удерживает файл тестовой базы. Проверка открытого сеанса не состоялась; независимые этапы продолжатся.'
        Read-Host 'Закройте окно или сообщение ТЕСТОВОЙ базы, если оно появилось, и нажмите Enter' | Out-Null
        Wait-Closed $Source
        return
    }
    $blocked = Invoke-Job (New-Job $Source $Destination $false) ($Tag + '-open-blocked')
    if ([string]$blocked.Run.Status -notin @('Failed','2') -or $blocked.Run.Backup) { throw 'Открытая база скопирована без разрешения.' }
    Add-Check ($Tag + '-open-blocked') 'passed' 'Настоящий открытый сеанс: без разрешения копия отклонена.'
    $shared = Invoke-Job (New-Job $Source $Destination $true) ($Tag + '-open-allowed')
    if (-not (Is-Succeeded $shared.Run) -or -not (Has-OpenWarning $shared.Run)) { throw 'Нет ожидаемого результата с предупреждением об открытой базе.' }
    Add-Check ($Tag + '-open-allowed') 'passed' 'Открытая база скопирована с предупреждением; согласованность такой копии не объявляется проверенной.'
    Read-Host 'Закройте окно ТЕСТОВОЙ базы и нажмите Enter' | Out-Null
    Wait-Closed $Source
    $again = Invoke-Job (New-Job $Source $Destination $true) ($Tag + '-closed-again')
    Assert-Archive $again.Run (Join-Path $Source '1Cv8.1CD') ($Tag + '-closed-again')
    if (Has-OpenWarning $again.Run) { throw 'После закрытия сеанса осталось ложное предупреждение.' }
    Add-Check ($Tag + '-closed-again') 'passed' 'После закрытия сеанса копирование снова успешно без предупреждения.'
}
try {
    $SourcePath = [IO.Path]::GetFullPath($SourcePath).TrimEnd('\')
    $sourceFile = Join-Path $SourcePath '1Cv8.1CD'
    if (-not (Test-Path -LiteralPath $sourceFile -PathType Leaf)) { throw 'В исходном каталоге нет 1Cv8.1CD.' }
    if ((Get-Item -LiteralPath $sourceFile).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Для проверки нужен настоящий файл базы.' }
    $programRoots = @($env:ProgramFiles, [Environment]::GetEnvironmentVariable('ProgramFiles(x86)'))
    if (-not $PlatformExecutable) {
        foreach ($base in $programRoots) {
            if ($base) {
                $candidate = Join-Path $base ("1cv8\$PlatformVersion\bin\1cv8.exe")
                if (Test-Path -LiteralPath $candidate) { $PlatformExecutable = $candidate; break }
            }
        }
    }
    if (-not $AgentExecutable) {
        foreach ($base in $programRoots) {
            if ($base) {
                foreach ($name in @('SP Backup','SP-Backup','1C-Backup')) {
                    $candidate = Join-Path $base ($name + '\SPBackup.Agent.exe')
                    if (Test-Path -LiteralPath $candidate) { $AgentExecutable = $candidate; break }
                }
            }
            if ($AgentExecutable) { break }
        }
    }
    if (-not $PlatformExecutable -or -not (Test-Path -LiteralPath $PlatformExecutable)) { throw 'Укажите -PlatformExecutable с полным путём к 1cv8.exe выбранной версии.' }
    if (-not $AgentExecutable -or -not (Test-Path -LiteralPath $AgentExecutable)) { throw 'Укажите -AgentExecutable с полным путём к установленному SPBackup.Agent.exe.' }
    $PlatformExecutable = [IO.Path]::GetFullPath($PlatformExecutable)
    $AgentExecutable = [IO.Path]::GetFullPath($AgentExecutable)
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($PlatformExecutable)
    $actualVersion = "$($version.FileMajorPart).$($version.FileMinorPart).$($version.FileBuildPart).$($version.FilePrivatePart)"
    if ($actualVersion -ne $PlatformVersion) { throw "Найдена платформа $actualVersion вместо $PlatformVersion." }
    $report.platformVersion = $actualVersion
    $report.agentVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($AgentExecutable).ProductVersion
    $root = [IO.Path]::GetFullPath((Join-Path $WorkParent ('onec-' + [Guid]::NewGuid().ToString('N'))))
    if ($root.StartsWith($SourcePath + '\', [StringComparison]::OrdinalIgnoreCase) -or $SourcePath.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Тестовый и исходный каталоги должны быть отдельными.' }
    $report.phase = 'workspace'
    New-Item -ItemType Directory -Path $root | Out-Null
    $ownsRoot = $true
    $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetOwner($owner); $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($owner.Value,'S-1-5-18','S-1-5-32-544') | Select-Object -Unique) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule ((New-Object Security.Principal.SecurityIdentifier $sid), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    Set-Acl -LiteralPath $root -AclObject $acl
    Write-Utf8 (Join-Path $root 'owned-test.json') '{"format":1,"owner":"SPBackup-OneC-acceptance"}'
    $report.phase = 'disk-space'
    $sourceBytes = (Get-Item -LiteralPath $sourceFile).Length
    $needed = $sourceBytes * 22 + 2GB
    $available = (New-Object IO.DriveInfo ([IO.Path]::GetPathRoot($root))).AvailableFreeSpace
    $report.diskSpace = [ordered]@{ sourceBytes = $sourceBytes; requiredBytes = $needed; availableBytes = $available }
    Write-Host ('Для полного набора проверок нужно {0:N1} ГиБ, свободно {1:N1} ГиБ.' -f ($needed / 1GB), ($available / 1GB))
    if ($available -lt $needed) {
        $report.errorCode = 'InsufficientDiskSpace'
        throw ('Недостаточно места: нужно {0:N1} ГиБ, свободно {1:N1} ГиБ. Добавьте -WorkParent с каталогом на другом диске. Копирование исходной базы ещё не начиналось.' -f ($needed / 1GB), ($available / 1GB))
    }
    if ($AskOneCLogin) {
        $report.phase = 'infobase-login'
        $probe = Start-Owned $AgentExecutable @('--supports-onec-credentials') $true
        try {
            $probeOut=$probe.StandardOutput.ReadToEndAsync(); $probeErr=$probe.StandardError.ReadToEndAsync()
            if(-not $probe.WaitForExit(10000)) { $probe.Kill(); throw 'Проверка поддержки входа ИБ не завершилась.' }
            $support=$probeOut.GetAwaiter().GetResult().Trim();$probeErr.GetAwaiter().GetResult() | Out-Null
            $supported=$probe.ExitCode -eq 0 -and $support -eq 'OneCInfobaseCredentials/1'
        } finally { $probe.Dispose() }
        if (-not $supported) {
            $report.errorCode = 'AgentInfobaseCredentialsUnsupported'
            throw 'Эта сборка агента не поддерживает вход пользователя ИБ. Укажите новую тестовую сборку через -AgentExecutable; пароль ещё не запрашивался.'
        }
        # Windows PowerShell can hide its graphical credential dialog behind the console.
        # Keep both prompts in this console; the password is read as a SecureString.
        $oneCUsername = Read-Host 'Имя пользователя информационной базы 1С (из списка входа в базу)'
        if ([string]::IsNullOrWhiteSpace($oneCUsername)) { throw 'Имя пользователя ИБ не введено. Проверка отменена до чтения исходной базы.' }
        $login = [Management.Automation.PSCredential]::new($oneCUsername, (Read-Host 'Пароль пользователя ИБ 1С (ввод скрыт)' -AsSecureString))
        $oneCPassword = $login.GetNetworkCredential().Password
        $oneCCredentialKey = [Guid]::NewGuid().ToString('N')
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = $AgentExecutable; $start.Arguments = '--store-onec-credentials ' + $oneCCredentialKey
        $start.UseShellExecute = $false; $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
        $save = [Diagnostics.Process]::Start($start)
        try {
            $out = $save.StandardOutput.ReadToEndAsync(); $err = $save.StandardError.ReadToEndAsync()
            $bytes = $utf8.GetBytes((ConvertTo-Json @{ username=$oneCUsername; password=$oneCPassword } -Compress))
            try { $save.StandardInput.BaseStream.Write($bytes,0,$bytes.Length); $save.StandardInput.Close() }
            finally { [Array]::Clear($bytes,0,$bytes.Length) }
            if (-not $save.WaitForExit(10000)) { $save.Kill(); throw 'Сохранение данных входа 1С не завершилось.' }
            $out.GetAwaiter().GetResult() | Out-Null; $err.GetAwaiter().GetResult() | Out-Null
            if ($save.ExitCode -ne 0) { throw 'Не удалось сохранить тестовые данные пользователя ИБ в Windows.' }
        } finally { $save.Dispose(); $login.Password.Dispose(); $login = $null }
        Add-Check 'infobase-login-stored' 'passed' 'Данные пользователя ИБ введены локально и сохранены в Windows только для этого теста; вход проверит настоящая выгрузка.'
    }
    $report.phase = 'isolated-source-copy'
    Write-Host 'Исходная база должна быть закрыта. Она откроется только для чтения на время создания отдельной копии.'
    $clone = Join-Path $root 'base'
    New-Item -ItemType Directory -Path $clone | Out-Null
    $sourceLease = New-Object IO.FileStream $sourceFile, 'Open', 'Read', 'None'
    try {
        $targetStream = New-Object IO.FileStream (Join-Path $clone '1Cv8.1CD'), 'CreateNew', 'Write', 'None'
        try {
            $buffer = New-Object byte[] 1048576; [long]$copied = 0
            while (($count = $sourceLease.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $targetStream.Write($buffer, 0, $count); $copied += $count
                Write-Progress -Activity 'Создание отдельной тестовой базы' -PercentComplete ([int](100 * $copied / $sourceLease.Length))
            }
            $targetStream.Flush($true)
            Write-Progress -Activity 'Создание отдельной тестовой базы' -Completed
        } finally { $targetStream.Dispose() }
    } finally { $sourceLease.Dispose() }
    Add-Check 'isolated-source-copy' 'passed' 'Тестовая база создана при эксклюзивном чтении исходника; запись в исходник не выполнялась.'
    if (-not $PlatformOnly) {
        $report.phase = 'local-file-cases'
        Test-FileCases $clone (Join-Path $root 'copies-local') 'local'
        $principal = New-Object Security.Principal.WindowsPrincipal ([Security.Principal.WindowsIdentity]::GetCurrent())
        if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            $report.phase = 'unc-file-cases'
            $shareName = 'SPB1CTest_' + [Guid]::NewGuid().ToString('N') + '$'
            New-SmbShare -Name $shareName -Path $root -FullAccess ([Security.Principal.WindowsIdentity]::GetCurrent().Name) | Out-Null
            $unc = '\\' + $env:COMPUTERNAME + '\' + $shareName
            Test-FileCases ($unc + '\base') ($unc + '\copies-unc') 'unc'
        } else { Add-Check 'unc' 'not-exercised' 'Для отдельной временной UNC-папки запустите сценарий от администратора.' }
    } else { Write-Host 'Выбран режим PlatformOnly: повторные ZIP/UNC-проверки не выполняются. Общая приёмка этим запуском не закрывается.' }
    Wait-Closed $clone
    $report.phase = 'platform-dump'
    $dump = Invoke-Job (New-Job $clone (Join-Path $root 'dumps') $false $true) 'platform-dump'
    if (-not (Is-Succeeded $dump.Run) -or -not $dump.Run.Backup -or (Get-Item -LiteralPath $dump.Run.Backup.Location).Length -le 0) { throw 'Платформа не создала успешную непустую выгрузку. Подробности остались локально.' }
    Add-Check 'platform-dump' 'passed' 'Настоящая платформа создала непустой DT через агент.'
    $report.phase = 'platform-restore'
    $restore = Join-Path $root 'restored-dt'
    New-Item -ItemType Directory -Path $restore | Out-Null
    Copy-Item -LiteralPath (Join-Path $clone '1Cv8.1CD') -Destination (Join-Path $restore '1Cv8.1CD')
    # This target is a separate copy of the test database and retains its infobase users.
    # Authenticate against that copy before replacing it with the DT contents.
    $restoreProcess = Start-AuthenticatedPlatform @('DESIGNER','/F',$restore,'/RestoreIB',[string]$dump.Run.Backup.Location,'/Out',(Join-Path $root 'restore-platform.log'),'/DisableStartupMessages','/DisableStartupDialogs')
    $ownedPlatforms.Add($restoreProcess)
    if (-not $restoreProcess.WaitForExit($DumpTimeoutMinutes * 60000)) { throw 'Истёк срок восстановления DT в отдельную тестовую базу.' }
    if ($restoreProcess.ExitCode -ne 0) { throw 'Платформа отклонила восстановление DT. Подробности остались в локальном журнале.' }
    Add-Check 'platform-restore' 'passed' 'Платформа восстановила DT в отдельную тестовую базу с кодом 0.'
    $restoredClient = Start-AuthenticatedPlatform @('ENTERPRISE','/F',$restore,'/DisableStartupMessages')
    $ownedPlatforms.Add($restoredClient)
    $answer = Read-Host 'Проверьте документы и справочники в ВОССТАНОВЛЕННОЙ тестовой базе. Если данные верны, введите ДА'
    Add-Check 'restored-data-human-check' $(if ($answer -in @('ДА','да','yes','YES')) { 'passed' } else { 'not-confirmed' }) 'Проверка данных DT пользователем: отдельный результат, не автоматическая проверка ZIP.'
    Read-Host 'Закройте окно восстановленной ТЕСТОВОЙ базы и нажмите Enter' | Out-Null
    Wait-Closed $restore
    $report.phase = 'platform-cancel'
    $cancel = Invoke-Job (New-Job $clone (Join-Path $root 'cancelled-dumps') $false $true) 'platform-cancel' $true
    if ($cancel.CancelSent) {
        if ([string]$cancel.Run.Status -notin @('Cancelled','3') -or $cancel.Run.Backup) { throw 'Отмена не дала ожидаемого результата.' }
        Wait-Closed $clone
        Add-Check 'platform-cancel' 'passed' 'Отменена настоящая выгрузка: агент завершился, копия не зарегистрирована, база освобождена.'
    } else { Add-Check 'platform-cancel' 'not-exercised' 'Выгрузка завершилась до отправки отмены; этот сценарий ещё не подтверждён.' }
    $report.selectedChecksPassed = @($checks | Where-Object { $_.status -ne 'passed' }).Count -eq 0
    $report.success = $report.selectedChecksPassed -and -not $PlatformOnly
    $report.phase = 'completed'
} catch {
    $report.errorType = $_.Exception.GetType().FullName
    Write-Warning $_.Exception.Message
} finally {
    foreach ($process in $ownedPlatforms) {
        try {
            if (-not $process.HasExited) {
                $process.CloseMainWindow() | Out-Null
                if (-not $process.WaitForExit(10000)) { $process.Kill(); $process.WaitForExit(10000) | Out-Null }
            }
            $process.Dispose()
        } catch { $report.success = $false; $report.errorType = 'OwnedPlatformCleanupFailed' }
    }
    foreach ($file in $ownedLoginFiles) {
        try { if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force } }
        catch { $report.success=$false; $report.selectedChecksPassed=$false; $report.errorType='OwnedLoginFileCleanupFailed' }
    }
    if ($oneCCredentialKey) {
        try { & $AgentExecutable '--remove-onec-credentials' $oneCCredentialKey | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Credential cleanup failed.' } }
        catch { $report.success=$false; $report.selectedChecksPassed=$false; $report.errorType='OwnedInfobaseCredentialCleanupFailed' }
    }
    $oneCPassword=$null; $oneCUsername=$null
    if ($login) { $login.Password.Dispose(); $login=$null }
    if ($shareName) {
        try {
            $share = Get-SmbShare -Name $shareName
            if ($share.Path -ne $root) { throw 'Временная папка SMB изменила принадлежность.' }
            Remove-SmbShare -Name $shareName -Force -Confirm:$false
        } catch { $report.success = $false; $report.errorType = 'OwnedShareCleanupFailed' }
    }
    $report.finishedUtc = [DateTime]::UtcNow.ToString('o')
    if ($ownsRoot -and (Test-Path -LiteralPath $root)) {
        $reportPath = Join-Path $root 'one-c-acceptance-result.json'
        Write-Utf8 $reportPath (ConvertTo-Json $report -Depth 10)
        Write-Host "Отчёт: $reportPath"
        Write-Host 'Передавайте только этот JSON. Базы, архивы и платформенные журналы содержат ваши данные и остаются локально.'
        Write-Host "Тестовые файлы оставлены в $root. После проверки эту отдельную папку можно удалить."
    }
}
if (-not $report.success) { exit 1 }
