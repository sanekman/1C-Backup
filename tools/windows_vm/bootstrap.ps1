$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$drive = Split-Path $PSScriptRoot -Qualifier
$root = 'C:\SPBLab'
New-Item -ItemType Directory -Path $root -Force | Out-Null
Copy-Item "$drive\owner.secret" "$root\owner.secret"
& icacls.exe "$root\owner.secret" /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not protect the disposable owner credential.' }
Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' -Name LongPathsEnabled -Type DWord -Value 1
Expand-Archive -LiteralPath "$drive\bundle.zip" -DestinationPath "$root\bundle"
Copy-Item "$drive\owner-task.ps1" "$root\owner-task.ps1"
$vc = Start-Process "$drive\vc_redist.x64.exe" -ArgumentList '/install','/quiet','/norestart' -PassThru -Wait
if ($vc.ExitCode -notin @(0,3010)) { throw "VC runtime installation failed: $($vc.ExitCode)" }
& icacls.exe "$root\bundle" /grant '*S-1-5-32-545:(OI)(CI)(RX)' /T /Q | Out-Null
& powercfg.exe /hibernate off
Get-NetConnectionProfile | Set-NetConnectionProfile -NetworkCategory Private
Enable-PSRemoting -SkipNetworkProfileCheck -Force
Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name LocalAccountTokenFilterPolicy -Type DWord -Value 1
Set-Service WinRM -StartupType Automatic
New-Item -ItemType Directory -Path "$root\results" -Force | Out-Null
# Remove only this generated answer after Windows has consumed it. The seed DVD is ejected by the host.
$answer = 'C:\Windows\Panther\unattend.xml'
if ((Test-Path $answer) -and (Get-Content $answer -Raw).Contains('SPBackup isolated VM')) { Remove-Item $answer -Force }
@{ ready = $true; version = (Get-CimInstance Win32_OperatingSystem).Version } | ConvertTo-Json | Set-Content "$root\ready.json" -Encoding UTF8
