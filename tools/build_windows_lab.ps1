[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Output,
    [Parameter(Mandatory)][string]$PostgreSqlRoot
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
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
        @{ Project = 'tests/WindowsAgentServiceSmoke'; Directory = 'probe' }
    )) {
        & dotnet publish $item.Project -c Release -r win-x64 --self-contained true -m:1 -nr:false `
            "-p:SourceRevisionId=$revision" -o (Join-Path $Output $item.Directory)
        if ($LASTEXITCODE -ne 0) { throw "Could not publish $($item.Project)" }
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
    $files = @(Get-ChildItem -LiteralPath $Output -File -Recurse | ForEach-Object {
        @{ path = $_.FullName.Substring($Output.Length + 1); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    @{ format = 1; revision = $revision; sdk = '10.0.402'; runtime = '10.0.13'; files = $files } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Output 'bundle.json') -Encoding UTF8
} finally { Pop-Location }
Write-Output "Lab bundle prepared at $Output; source $revision. No release was published."
