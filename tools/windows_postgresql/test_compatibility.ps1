param([string]$Output = 'artifacts/postgresql-compatibility')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'This fixture requires a disposable GitHub-hosted Windows runner.'
}
$outputPath = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force $outputPath | Out-Null
$source = (& git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the exact source.' }
$private = Join-Path $env:RUNNER_TEMP ('spb-pg-compat-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $private | Out-Null
$phase = 'download-tools'
$tools = @()
$oldSource = $env:SPBACKUP_COMPAT_SOURCE; $oldTools = $env:SPBACKUP_COMPAT_TOOLS
try {
    # Fixed official vendor URLs. Actual SHA-256 and executable versions are retained in the result.
    foreach ($item in @(
        @{ Major=15; Version='15.19-5' }, @{ Major=16; Version='16.15-5' },
        @{ Major=17; Version='17.11-5' }, @{ Major=18; Version='18.6-5' }
    )) {
        $phase = "download-$($item.Major)"
        $url = "https://get.enterprisedb.com/postgresql/postgresql-$($item.Version)-windows-x64-binaries.zip"
        $archive = Join-Path $private 'download.zip'
        Invoke-WebRequest -Uri $url -OutFile $archive -TimeoutSec 300
        $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
        $destination = Join-Path $private ([string]$item.Major)
        New-Item -ItemType Directory $destination | Out-Null
        $zip = [IO.Compression.ZipFile]::OpenRead($archive)
        try {
            foreach ($entry in $zip.Entries) {
                if ($entry.FullName -notmatch '^pgsql/(bin|lib|share)/' -or $entry.FullName.EndsWith('/')) { continue }
                $target = [IO.Path]::GetFullPath((Join-Path $destination $entry.FullName))
                if (-not $target.StartsWith($destination + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive path escapes its owned directory.' }
                New-Item -ItemType Directory -Force ([IO.Path]::GetDirectoryName($target)) | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $false)
            }
        } finally { $zip.Dispose() }
        Remove-Item -LiteralPath $archive
        $bin = Join-Path $destination 'pgsql/bin'
        foreach ($name in @('initdb','pg_ctl','postgres','psql','pg_dump','pg_restore')) {
            if (-not (Test-Path -LiteralPath (Join-Path $bin ($name + '.exe')) -PathType Leaf)) { throw 'Required PostgreSQL binary is missing.' }
        }
        $tools += @{ Major=$item.Major; Bin=$bin; Url=$url; Sha256=$hash }
    }
    $manifest = Join-Path $private 'tools.json'
    ConvertTo-Json -InputObject @($tools) -Depth 4 | Set-Content -LiteralPath $manifest -Encoding utf8
    $phase = 'publish-probe'
    & dotnet publish tests/WindowsPostgreSqlCompatibilitySmoke -c Release -r win-x64 --self-contained true -o (Join-Path $private 'probe') -m:1 -nr:false
    if ($LASTEXITCODE -ne 0) { throw 'Compatibility probe did not build.' }
    $phase = 'native-compatibility-matrix'
    $env:SPBACKUP_COMPAT_SOURCE = $source; $env:SPBACKUP_COMPAT_TOOLS = $manifest
    & (Join-Path $private 'probe/WindowsPostgreSqlCompatibilitySmoke.exe') $outputPath
    if ($LASTEXITCODE -ne 0) { throw 'Native compatibility matrix failed; inspect its sanitized result.' }
    $result = Get-Content -LiteralPath (Join-Path $outputPath 'postgresql-compatibility-result.json') -Raw | ConvertFrom-Json
    if (-not $result.success -or $result.source -ne $source -or @($result.cases).Count -ne 18) { throw 'Required compatibility checks did not finish.' }
} catch {
    @{ success=$false; source=$source; phase=$phase; error=$_.Exception.GetType().Name;
       toolchains=@($tools | ForEach-Object { @{ Major=$_.Major; Url=$_.Url; Sha256=$_.Sha256 } }) } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputPath 'postgresql-setup-result.json') -Encoding utf8
    throw
} finally {
    $env:SPBACKUP_COMPAT_SOURCE = $oldSource; $env:SPBACKUP_COMPAT_TOOLS = $oldTools
    # Only tools downloaded into this newly created directory are removed.
    if (Test-Path -LiteralPath $private) { Remove-Item -LiteralPath $private -Recurse -Force }
}
