$ErrorActionPreference = 'Stop'
$plan = $null
$resultPath = Join-Path (Split-Path -Parent $planPath) 'update-result.json'
try {
    $plan = Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json
    $parent = Get-Process -Id $plan.ParentId -ErrorAction SilentlyContinue
    if ($parent -and $parent.StartTime.ToUniversalTime().Ticks -eq [long]$plan.ParentStartedTicks) {
        if (-not $parent.WaitForExit(180000)) { throw 'Программа не завершилась. Обновление не установлено.' }
    }
    $updatesRoot = [IO.Path]::GetFullPath((Split-Path -Parent $planPath)).TrimEnd('\')
    $installer = [IO.Path]::GetFullPath($plan.Installer)
    if ([IO.Path]::GetDirectoryName($installer) -ne $updatesRoot -or [IO.Path]::GetFileName($installer) -notmatch '^[a-f0-9]{64}\.exe$') { throw 'Некорректный путь установщика.' }
    $item = Get-Item -LiteralPath $installer
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Установщик не должен быть ссылкой.' }
    if ($item.Length -ne [long]$plan.Size -or (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ne $plan.Sha256) { throw 'Проверка скачанного установщика не пройдена.' }
    $registered = $false
    foreach ($hive in @('HKLM', 'HKCU')) {
        try {
            $location = (Get-ItemProperty -LiteralPath ($hive + ':\Software\Microsoft\Windows\CurrentVersion\Uninstall\{142FA512-738F-4BE8-9C31-487B3500B30B}_is1') -ErrorAction Stop).InstallLocation
            if ($location -and [IO.Path]::IsPathRooted($location) -and
                [IO.Path]::GetFullPath($location).TrimEnd('\') -eq [IO.Path]::GetFullPath($plan.InstallDirectory).TrimEnd('\')) { $registered = $true; break }
        }
        catch { # A missing/invalid machine entry must not hide a valid legacy user install.
        }
    }
    if (-not $registered) { throw 'Папка установленной программы изменилась. Обновление отменено.' }
    # NSIS requires /D to be the last argument and its directory must not be quoted.
    if ($plan.InstallerKind -eq 'nsis') { $arguments = @('/S', ('/D=' + $plan.InstallDirectory)) }
    elseif ($plan.InstallerKind -eq 'inno') { $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/DIR="' + $plan.InstallDirectory + '"')) }
    else { throw 'Неизвестный формат установщика.' }
    $install = Start-Process -FilePath $installer -ArgumentList $arguments -PassThru -Wait
    if ($install.ExitCode -ne 0) { throw ('Установщик завершился с кодом ' + $install.ExitCode) }
    $versionFile = Join-Path $plan.InstallDirectory 'VERSION.txt'
    if ((Get-Content -LiteralPath $versionFile -Raw).Trim() -ne $plan.Version) { throw 'Версия после установки не совпадает с ожидаемой.' }
    @{ Success = $true; Version = $plan.Version; TimeUtc = [DateTime]::UtcNow.ToString('o'); Message = 'Обновление установлено.' } | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
    if ($plan.Restart) {
        $application = Join-Path $plan.InstallDirectory 'SPBackup.Desktop.exe'
        if (-not (Test-Path -LiteralPath $application)) { $application = Join-Path $plan.InstallDirectory 'OneCBackup.Desktop.exe' }
        if ($plan.RestartAgent) { Start-Process -FilePath $application -ArgumentList '--start-agent' }
        elseif ($plan.RestartWindowSchedule) { Start-Process -FilePath $application -ArgumentList '--start-window-scheduler' }
        else { Start-Process -FilePath $application }
    }
    Remove-Item -LiteralPath $planPath -Force
}
catch {
    @{ Success = $false; Version = $plan.Version; TimeUtc = [DateTime]::UtcNow.ToString('o'); Message = $_.Exception.Message } | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
    # Reopen the existing application to display the failed update report, without restarting the agent.
    if ($plan.Restart) {
        $application = Join-Path $plan.InstallDirectory 'SPBackup.Desktop.exe'
        if (-not (Test-Path -LiteralPath $application)) { $application = Join-Path $plan.InstallDirectory 'OneCBackup.Desktop.exe' }
        if (Test-Path -LiteralPath $application) { Start-Process -FilePath $application }
    }
    exit 1
}
