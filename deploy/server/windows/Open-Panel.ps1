$ErrorActionPreference='Stop'
# A parent PowerShell 7 process may pass incompatible module paths to Windows PowerShell 5.1.
if ($PSVersionTable.PSEdition -eq 'Desktop') { $env:PSModulePath=Join-Path $PSHOME 'Modules' }
$url=[IO.File]::ReadAllText((Join-Path $env:ProgramData 'OneCBackupServer\panel-url.txt')).Trim()
$address=$null
if ($url -match '[\s\\]' -or ![Uri]::TryCreate($url,[UriKind]::Absolute,[ref]$address) -or $address.Scheme -ne 'https' -or
    !$address.Host -or $address.UserInfo -or $address.Query -or $address.Fragment) { throw 'Invalid panel address.' }
Start-Process $url
