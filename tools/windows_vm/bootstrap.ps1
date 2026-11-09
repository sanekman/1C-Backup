param([switch]$StageOnly)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$drive = Split-Path $PSScriptRoot -Qualifier
if ($StageOnly) {
    # Do not run MSI/WinRM installation while Windows Setup holds its own setup locks.
    $scripts = 'C:\Windows\Setup\Scripts'
    New-Item -ItemType Directory -Path $scripts -Force | Out-Null
    @('@echo off', "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$PSScriptRoot\bootstrap.ps1`" > C:\SPBLab-bootstrap.log 2>&1", 'exit /b 0') |
        Set-Content "$scripts\SetupComplete.cmd" -Encoding ASCII
    exit 0
}
$serial = New-Object IO.Ports.SerialPort 'COM1',115200,'None',8,'One'
$serial.Open()
function Write-Step([string]$message) {
    $line = "$(Get-Date -Format o) $message"
    $serial.WriteLine($line)
    Write-Output $line
}
trap { Write-Step ("FAILED: " + $_.Exception.Message); $serial.Close(); exit 1 }
Write-Step 'Protecting the owned VM credential'
$root = 'C:\SPBLab'
New-Item -ItemType Directory -Path $root -Force | Out-Null
Copy-Item "$drive\owner.secret" "$root\owner.secret"
& icacls.exe "$root\owner.secret" /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not protect the disposable owner credential.' }
Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' -Name LongPathsEnabled -Type DWord -Value 1
Write-Step 'Extracting the verified application and PostgreSQL binaries'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory("$drive\bundle.zip", "$root\bundle")
Copy-Item "$drive\owner-task.ps1" "$root\owner-task.ps1"
Write-Step 'Installing the PostgreSQL VC runtime'
$vc = Start-Process "$drive\vc_redist.x64.exe" -ArgumentList '/install','/quiet','/norestart' -PassThru
if (-not $vc.WaitForExit(180000)) { $vc.Kill(); throw 'Owned VC runtime installation timed out.' }
if ($vc.ExitCode -notin @(0,3010)) { throw "VC runtime installation failed: $($vc.ExitCode)" }
Write-Step 'Granting binary execution access'
& icacls.exe "$root\bundle" /grant '*S-1-5-32-545:(OI)(CI)(RX)' /T /Q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not grant access to the owned bundle.' }
& powercfg.exe /hibernate off
Write-Step 'Enabling private WinRM'
Get-NetConnectionProfile | Set-NetConnectionProfile -NetworkCategory Private
Enable-PSRemoting -SkipNetworkProfileCheck -Force
Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name LocalAccountTokenFilterPolicy -Type DWord -Value 1
Set-Service WinRM -StartupType Automatic
New-Item -ItemType Directory -Path "$root\results" -Force | Out-Null
# Remove only this generated answer after Windows has consumed it. The seed DVD is ejected by the host.
$answer = 'C:\Windows\Panther\unattend.xml'
if ((Test-Path $answer) -and (Get-Content $answer -Raw).Contains('SPBackup isolated VM')) { Remove-Item $answer -Force }
@{ ready = $true; version = (Get-CimInstance Win32_OperatingSystem).Version } | ConvertTo-Json | Set-Content "$root\ready.json" -Encoding UTF8
Write-Step 'Ready; waiting for Windows Setup to finish'
$serial.Close()
