[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Output,
    [Parameter(Mandatory)][string]$PostgreSqlRoot
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
foreach ($script in Get-ChildItem (Join-Path $PSScriptRoot 'windows_vm') -Filter '*.ps1') {
    $parseErrors = $null; $tokens = $null
    $null = [Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors) { throw ($parseErrors | Out-String) }
}
$Output = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $Output) { throw 'Use a new output directory; an existing bundle is never replaced.' }
if ((& dotnet --version).Trim() -ne '10.0.402') { throw 'Build the lab bundle with .NET SDK 10.0.402.' }
foreach ($part in @('bin\pg_dump.exe', 'bin\pg_ctl.exe', 'bin\initdb.exe', 'share\postgres.bki')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PostgreSqlRoot $part))) { throw "Missing PostgreSQL distribution component: $part" }
}
$revision = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or (& git -C $repo status --porcelain)) { throw 'Build from a clean committed source tree.' }
New-Item -ItemType Directory -Path $Output | Out-Null
Push-Location $repo
try {
    foreach ($item in @(
        @{ Project = 'src/SPBackup.Agent'; Directory = 'agent' },
        @{ Project = 'tests/WindowsAgentServiceSmoke'; Directory = 'probe' },
        @{ Project = 'src/SPBackup.Desktop'; Directory = 'desktop' }
    )) {
        & dotnet publish $item.Project -c Release -r win-x64 --self-contained true -m:1 -nr:false `
            "-p:SourceRevisionId=$revision" -o (Join-Path $Output $item.Directory)
        if ($LASTEXITCODE -ne 0) { throw "Could not publish $($item.Project)" }
    }
    # Preserve desktop framework implementations, notably WindowsBase.dll: the
    # console runtime contains a facade with the same name, without WPF types.
    $desktopBase = Join-Path $Output 'desktop\WindowsBase.dll'
    $desktopBaseHash = (Get-FileHash -LiteralPath $desktopBase -Algorithm SHA256).Hash
    $agentRoot = Join-Path $Output 'agent'
    foreach ($file in Get-ChildItem -LiteralPath $agentRoot -File -Recurse) {
        $relative = $file.FullName.Substring($agentRoot.Length + 1)
        $target = Join-Path (Join-Path $Output 'desktop') $relative
        if (-not (Test-Path -LiteralPath $target)) {
            New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $target
        }
    }
    if ((Get-FileHash -LiteralPath $desktopBase -Algorithm SHA256).Hash -ne $desktopBaseHash) {
        throw 'Merging the agent replaced the desktop framework.'
    }
    $desktopSmoke = Join-Path (Split-Path $Output -Parent) 'lab-desktop-smoke'
    $fixtureProcess = Start-Process -FilePath (Join-Path $Output 'desktop\SPBackup.Desktop.exe') `
        -ArgumentList @('--smoke-ui', ('"' + $desktopSmoke + '"')) -PassThru
    if (-not $fixtureProcess.WaitForExit(120000)) { $fixtureProcess.Kill(); throw 'The bundled desktop smoke test timed out.' }
    if ($fixtureProcess.ExitCode -ne 0 -or -not (Get-Content (Join-Path $desktopSmoke 'smoke-result.json') -Raw | ConvertFrom-Json).success) {
        throw 'The merged desktop and agent bundle failed its real WPF smoke test.'
    }
    & dotnet build tests/SPBackup.Core.Tests -c Release -m:1 -nr:false -o (Join-Path $Output 'tests')
    if ($LASTEXITCODE -ne 0) { throw 'Could not build the actual SMB delivery tests.' }
    foreach ($part in @('bin', 'lib', 'share')) {
        New-Item -ItemType Directory -Force -Path (Join-Path $Output 'postgresql') | Out-Null
        Copy-Item -LiteralPath (Join-Path $PostgreSqlRoot $part) -Destination (Join-Path $Output "postgresql\$part") -Recurse
    }
    # The test runner needs the Windows SDK, not the Linux SDK or an installation on the guest.
    $sdkLine = & dotnet --list-sdks | Where-Object { $_ -match '^10\.0\.402 \[' }
    if (@($sdkLine).Count -ne 1) { throw 'Could not locate SDK 10.0.402.' }
    $dotnetRoot = Split-Path ([regex]::Match($sdkLine, '\[(.*)\]').Groups[1].Value) -Parent
    New-Item -ItemType Directory -Force -Path (Join-Path $Output 'dotnet') | Out-Null
    Copy-Item -LiteralPath (Join-Path $dotnetRoot 'dotnet.exe') -Destination (Join-Path $Output 'dotnet\dotnet.exe')
    foreach ($part in @('sdk\10.0.402', 'host\fxr\10.0.13', 'shared\Microsoft.NETCore.App\10.0.13', 'shared\Microsoft.AspNetCore.App\10.0.13')) {
        $target = Join-Path $Output "dotnet\$part"
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
        Copy-Item -LiteralPath (Join-Path $dotnetRoot $part) -Destination $target -Recurse
    }
    New-Item -ItemType Directory -Path (Join-Path $Output 'controller') | Out-Null
    Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*windows_lab*.ps1' | Copy-Item -Destination (Join-Path $Output 'controller')
    Copy-Item -LiteralPath (Join-Path $repo 'docs/windows-lab.md') -Destination (Join-Path $Output 'controller')
    # Exercise the copied SDK/runner itself; a system SDK passing tests does not prove portability.
    $runner = Join-Path $Output 'dotnet\dotnet.exe'
    if ((& $runner --version).Trim() -ne '10.0.402' -or $LASTEXITCODE -ne 0) { throw 'The copied Windows SDK cannot run.' }
    & $runner vstest (Join-Path $Output 'tests\SPBackup.Core.Tests.dll') `
        '/TestCaseFilter:FullyQualifiedName~LocalManualRunQueueTests' `
        "/ResultsDirectory:$(Join-Path (Split-Path $Output -Parent) 'lab-runner-smoke')"
    if ($LASTEXITCODE -ne 0) { throw 'The copied test runner failed its real manual-queue tests.' }
    $files = @(Get-ChildItem -LiteralPath $Output -File -Recurse | ForEach-Object {
        @{ path = $_.FullName.Substring($Output.Length + 1); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    @{ format = 1; revision = $revision; sdk = '10.0.402'; runtime = '10.0.13'; runnerVerified = $true; desktopVerified = $true; files = $files } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Output 'bundle.json') -Encoding UTF8
} finally { Pop-Location }
Write-Output "Lab bundle prepared at $Output; source $revision. No release was published."
