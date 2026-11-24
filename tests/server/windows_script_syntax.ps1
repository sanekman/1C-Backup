# Parse the exact scripts with Windows PowerShell 5.1 before installing anything.
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5) { throw 'Windows PowerShell 5.1 is required.' }
$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$files=@(Get-ChildItem (Join-Path $root 'deploy/server/windows') -Filter '*.ps1')+@(Get-ChildItem $PSScriptRoot -Filter 'windows_*.ps1')
foreach ($file in $files) {
    $tokens=$null;$errors=$null
    $null=[Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$errors)
    if (@($errors).Count) { throw ($file.Name+': '+(($errors | ForEach-Object Message) -join '; ')) }
}
Write-Output ('Windows PowerShell 5.1: parsed '+$files.Count+' scripts.')
