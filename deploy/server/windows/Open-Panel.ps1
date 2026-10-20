$ErrorActionPreference='Stop'
# A parent PowerShell 7 process may pass incompatible module paths to Windows PowerShell 5.1.
if ($PSVersionTable.PSEdition -eq 'Desktop') { $env:PSModulePath=Join-Path $PSHOME 'Modules' }
$url=[IO.File]::ReadAllText((Join-Path $env:ProgramData 'OneCBackupServer\panel-url.txt')).Trim()
if ($url -notmatch '^https://[a-zA-Z0-9.-]+:7443/$') { throw 'Invalid panel address.' }
Start-Process $url
