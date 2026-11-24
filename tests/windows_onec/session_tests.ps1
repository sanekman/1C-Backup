$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$errors = $null; $tokens = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'tools\test_windows_onec.ps1'), [ref]$tokens, [ref]$errors)
if ($errors) { throw ($errors | Out-String) }
$definition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-FileCases'
}, $true)
if (-not $definition) { throw 'The session controller function was not found.' }
. ([scriptblock]::Create($definition.Extent.Text))

# Controller test only: an ordinary unlocked file and a fake platform process
# reproduce the observed absence of an open session. No real 1C is invoked.
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('sp-onec-session-' + [Guid]::NewGuid().ToString('N'))
$ownedPlatforms = New-Object 'Collections.Generic.List[object]'
$recorded = New-Object 'Collections.Generic.List[object]'
$script:invocations = 0; $script:restores = 0; $script:closedChecks = 0; $script:prompts = 0
$PlatformExecutable = 'controller-only-no-executable'
function Wait-Closed([string]$Directory) { $script:closedChecks++ }
function New-Job { return [pscustomobject]@{ fixture = $true } }
function Invoke-Job {
    $script:invocations++
    if ($script:invocations -gt 2) { throw 'An unconfirmed open session was used to run a backup.' }
    return [pscustomobject]@{ Run = [pscustomobject]@{ Status = 'Succeeded' } }
}
function Assert-Archive { $script:restores++ }
function Has-OpenWarning { return $false }
function Start-Owned { return [pscustomobject]@{ fixture = $true } }
function Read-Host { $script:prompts++; return '' }
function Add-Check([string]$Name, [string]$Status, [string]$Note) {
    $recorded.Add([pscustomobject]@{ Name = $Name; Status = $Status })
}
New-Item -ItemType Directory -Path $fixture | Out-Null
try {
    [IO.File]::WriteAllText((Join-Path $fixture '1Cv8.1CD'), 'An unlocked controller fixture, not a database.')
    Test-FileCases $fixture (Join-Path $fixture 'unused-destination') 'unc'
    if ($script:invocations -ne 2 -or $script:restores -ne 2) { throw 'Completed closed-source checks were lost or repeated.' }
    if ($script:closedChecks -ne 2 -or $script:prompts -ne 2) { throw 'The controller did not require closing the test window and checking the file before continuing.' }
    if ($ownedPlatforms.Count -ne 1) { throw 'The test platform did not remain tracked for cleanup.' }
    if (@($recorded | Where-Object { $_.Status -eq 'passed' }).Count -ne 2) { throw 'Unexpected acceptance was recorded.' }
    $missed = @($recorded | Where-Object { $_.Name -eq 'unc-open-session' -and $_.Status -eq 'not-exercised' })
    if ($missed.Count -ne 1 -or $recorded.Count -ne 3) { throw 'Missing session was not explicitly left unverified.' }
    @{ success = $true; scope = 'controller-session-only'; actualOneC = $false; checks = @(
        'missing-session-does-not-abort-independent-stages',
        'missing-session-never-reports-open-backup-success',
        'window-close-and-file-release-required-before-return'
    ) } | ConvertTo-Json -Compress
} finally { Remove-Item -LiteralPath $fixture -Recurse -Force }
exit 0
