param([string]$Case, [string]$FixtureBase, [string]$Source)
$ErrorActionPreference = 'Stop'
$fixtureRoot = Join-Path $FixtureBase $Case
$updates = Join-Path $fixtureRoot 'updates'
$installed = Join-Path $fixtureRoot 'application with spaces'
New-Item -ItemType Directory -Path $updates,$installed -Force | Out-Null
$version = '0.1.0-preview.999'
$payload = [Text.Encoding]::UTF8.GetBytes('MZinstaller fixture')
$algorithm = [Security.Cryptography.SHA256]::Create()
try { $sha = [BitConverter]::ToString($algorithm.ComputeHash($payload)).Replace('-','').ToLowerInvariant() } finally { $algorithm.Dispose() }
$installer = Join-Path $updates ($sha + '.exe')
[IO.File]::WriteAllBytes($installer,$payload)
if ($Case -eq 'corrupt') { [IO.File]::WriteAllText($installer,'MZcorrupt payload') }
$start = [DateTime]::Now
$plan = @{ ParentId = 123; ParentStartedTicks = $start.ToUniversalTime().Ticks; Installer = $installer; Size = $payload.Length; Sha256 = $sha;
Version = $version; InstallDirectory = $installed; Restart = ($Case -eq 'restart'); RestartAgent = ($Case -eq 'restart'); RestartWindowSchedule = $false;
InstallerKind = $(if($Case -eq 'inno') {'inno'} else {'nsis'}) }
$planPath = Join-Path $updates 'install-plan-fixture.json'
$plan | ConvertTo-Json | Set-Content -LiteralPath $planPath -Encoding UTF8
[IO.File]::WriteAllText((Join-Path $installed 'VERSION.txt'),$(if($Case -eq 'wrong-version') {'0.1.0-preview.39'} else {$version}))
function Get-Process {
 [CmdletBinding()] param([int]$Id)
 if ($Case -eq 'parent-timeout') {
  $parent = [pscustomobject]@{ StartTime = $start }
  $parent | Add-Member ScriptMethod WaitForExit { param($timeout) return $false }
  return $parent
 }
 return $null
}
function Get-ItemProperty {
 [CmdletBinding()] param([string]$LiteralPath)
 return [pscustomobject]@{ InstallLocation = $(if($Case -eq 'wrong-directory') {(Join-Path $FixtureBase 'other')} else {$installed}) }
}
function Start-Process {
 [CmdletBinding()] param([string]$FilePath,[string[]]$ArgumentList,[switch]$PassThru,[switch]$Wait)
 [pscustomobject]@{ FilePath=$FilePath; Arguments=$ArgumentList; Wait=[bool]$Wait } | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $fixtureRoot 'calls.jsonl') -Encoding UTF8
 if ($PassThru) { return [pscustomobject]@{ ExitCode = $(if($Case -eq 'installer-failure') {1} else {0}) } }
}
$global:LASTEXITCODE = 0
. ([scriptblock]::Create([IO.File]::ReadAllText($Source,[Text.Encoding]::UTF8)))
exit $LASTEXITCODE
