$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$script = Join-Path $repo 'tools\test_windows_onec.ps1'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('sp-onec-guards-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
try {
    $source = Join-Path $fixture 'source'
    New-Item -ItemType Directory -Path $source | Out-Null
    $database = Join-Path $source '1Cv8.1CD'
    [IO.File]::WriteAllText($database, 'Controller guard fixture; not a real 1C database.')
    $before = (Get-FileHash -LiteralPath $database -Algorithm SHA256).Hash
    $native = Join-Path $env:WINDIR 'System32\notepad.exe'
    $shell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($native)
    $actual = "$($version.FileMajorPart).$($version.FileMinorPart).$($version.FileBuildPart).$($version.FilePrivatePart)"
    $unusedWork = Join-Path $fixture 'must-not-be-created'
    & $shell -NoProfile -ExecutionPolicy Bypass -File $script -SourcePath $source -PlatformVersion '8.5.1.1150' -PlatformExecutable $native -AgentExecutable $native -WorkParent $unusedWork | Out-Null
    if ($LASTEXITCODE -ne 1 -or (Test-Path -LiteralPath $unusedWork)) { throw 'Wrong platform was not rejected before creating a workspace.' }
    & $shell -NoProfile -ExecutionPolicy Bypass -File $script -SourcePath $source -PlatformVersion $actual -PlatformExecutable $native -AgentExecutable $native -WorkParent $source | Out-Null
    if ($LASTEXITCODE -ne 1) { throw 'A workspace inside the source was accepted.' }
    if ((Get-FileHash -LiteralPath $database -Algorithm SHA256).Hash -ne $before) { throw 'A rejected controller run changed its source.' }
    if (@(Get-ChildItem -LiteralPath $source).Count -ne 1) { throw 'A rejected controller run wrote into its source.' }
    & $shell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'session_tests.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'The unconfirmed-session controller check failed on Windows PowerShell.' }
    @{ success = $true; scope = 'controller-safety-only'; actualOneC = $false; checks = @(
        'wrong-platform-rejected-before-workspace-creation',
        'workspace-inside-source-rejected',
        'rejected-runs-preserve-source-bytes-and-directory'
    ) } | ConvertTo-Json -Compress
} finally { Remove-Item -LiteralPath $fixture -Recurse -Force }
# Both negative probes intentionally return 1. Do not leak their native exit code
# to the CI shell after all source-preservation assertions have passed.
exit 0
