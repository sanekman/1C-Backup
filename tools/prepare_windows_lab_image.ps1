# Run INSIDE an empty disposable Hyper-V guest, never on a workstation/server with data.
#requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)][PSCredential]$Administrator,
    [Parameter(Mandatory)][switch]$PrepareDisposableImage
)
$ErrorActionPreference = 'Stop'
if (-not $PrepareDisposableImage -or (Get-CimInstance Win32_ComputerSystem).Model -ne 'Virtual Machine') {
    throw 'This command is only for an explicitly disposable Hyper-V image.'
}
if ($Administrator.UserName -ne 'Administrator' -or $Administrator.GetNetworkCredential().Password.Length -lt 14) {
    throw 'Use the English Windows Server local Administrator account and a unique test password of at least 14 characters.'
}
if (Get-Service OneCBackupAgent -ErrorAction SilentlyContinue) { throw 'The base image must not contain an SP Backup service.' }
$directory = 'C:\SPBackupLabImage'
$setup = 'C:\Windows\Setup\Scripts\SetupComplete.cmd'
if ((Test-Path $directory) -or (Test-Path $setup)) { throw 'Refusing to replace existing image preparation/setup scripts.' }
New-Item -ItemType Directory $directory | Out-Null
$acl = New-Object Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)
foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        (New-Object Security.Principal.SecurityIdentifier($sid)), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
}
Set-Acl -LiteralPath $directory -AclObject $acl
$password = [Security.SecurityElement]::Escape($Administrator.GetNetworkCredential().Password)
$answer = @"
<?xml version="1.0" encoding="utf-8"?>
<unattend xmlns="urn:schemas-microsoft-com:unattend">
  <settings pass="specialize">
    <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <ComputerName>*</ComputerName>
    </component>
  </settings>
  <settings pass="oobeSystem">
    <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <OOBE><HideEULAPage>true</HideEULAPage><HideLocalAccountScreen>true</HideLocalAccountScreen><HideOnlineAccountScreens>true</HideOnlineAccountScreens><HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE><ProtectYourPC>3</ProtectYourPC></OOBE>
      <UserAccounts><AdministratorPassword><Value>$password</Value><PlainText>true</PlainText></AdministratorPassword></UserAccounts>
    </component>
  </settings>
</unattend>
"@
$path = Join-Path $directory 'unattend.xml'
$answer | Set-Content -LiteralPath $path -Encoding UTF8
$answer = $null; $password = $null
New-Item -ItemType Directory -Force (Split-Path $setup -Parent) | Out-Null
@'
@echo off
del /f /q C:\Windows\Panther\unattend.xml 2>nul
del /f /q C:\Windows\Panther\Unattend\unattend.xml 2>nul
del /f /q C:\SPBackupLabImage\unattend.xml 2>nul
del /f /q "%~f0"
'@ | Set-Content -LiteralPath $setup -Encoding Ascii
Write-Output 'Generalizing and shutting down this disposable image. Do not boot the parent VHDX again; create differencing guests from it.'
& 'C:\Windows\System32\Sysprep\Sysprep.exe' /generalize /oobe /shutdown "/unattend:$path"
if ($LASTEXITCODE -ne 0) { throw 'Sysprep failed. Inspect the image; it is not a ready lab base.' }
