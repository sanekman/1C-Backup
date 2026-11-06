param(
 [Parameter(Mandatory=$true)][string]$SiteName,
 [Parameter(Mandatory=$true)][string]$Domain,
 [Parameter(Mandatory=$true)][string]$Email,
 [Parameter(Mandatory=$true)][string]$WinAcmePath
)
$ErrorActionPreference = 'Stop'
if ($Domain -notmatch '^[a-zA-Z0-9](?:[a-zA-Z0-9.-]*[a-zA-Z0-9])?$' -or $Domain.Contains('..') -or -not $Domain.Contains('.')) { throw 'Specify a DNS domain.' }
if (-not (Test-Path -LiteralPath $WinAcmePath -PathType Leaf)) { throw 'Download win-acme from its official release and specify the full path to wacs.exe.' }
Import-Module WebAdministration
$site = Get-Website -Name $SiteName
if (-not $site) { throw 'Create the IIS site with its HTTP domain binding and reverse proxy configuration first.' }
if (-not (Get-WebBinding -Name $SiteName -Protocol http | Where-Object { $_.bindingInformation -like "*:$Domain" })) { throw 'The IIS site needs an HTTP binding for the requested domain.' }
& $WinAcmePath --source iis --siteid $site.id --host $Domain --emailaddress $Email --accepttos --validation selfhosting --installation iis --store certificatestore --baseuri https://acme-v02.api.letsencrypt.org/
if ($LASTEXITCODE -ne 0) { throw 'Certificate issuance failed. Check DNS, inbound validation traffic and win-acme logs.' }
Write-Output 'Certificate obtained and installed. Verify win-acme automatic renewal task and HTTPS/WebSocket publication.'
