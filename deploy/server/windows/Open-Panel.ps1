$ErrorActionPreference='Stop'
$url=[IO.File]::ReadAllText((Join-Path $env:ProgramData 'OneCBackupServer\panel-url.txt')).Trim()
if ($url -notmatch '^https://[a-zA-Z0-9.-]+:7443/$') { throw 'Invalid panel address.' }
Start-Process $url
