param([Parameter(Mandatory)][ValidateSet('volumes','switch','prepare','resume','user-prepare','user-verify')][string]$Phase)
$ErrorActionPreference = 'Stop'
$root = 'C:\SPBLab'
$out = "$root\results\reboot"
New-Item -ItemType Directory -Path $out -Force | Out-Null
$env:ONECBACKUP_NATIVE_SERVICE_TEST = '1'
$env:GITHUB_SHA = (Get-Content "$root\bundle\bundle.json" -Raw | ConvertFrom-Json).revision
$env:PGBIN = "$root\bundle\postgresql\bin"
$env:PATH = "$env:PGBIN;$root\bundle\dotnet;$env:PATH"
$env:DOTNET_ROOT = "$root\bundle\dotnet"
try {
    $mode = switch ($Phase) {
        'volumes' { '--verify-volumes' }
        'switch' { '--verify-service-switch' }
        'prepare' { '--prepare-reboot' }
        'resume' { '--verify-reboot' }
        'user-prepare' { '--prepare-user-startup' }
        'user-verify' { '--verify-user-startup' }
    }
    if ($Phase -eq 'volumes') { $out = "$root\results\volumes" }
    if ($Phase -eq 'switch') { $out = "$root\results\switch" }
    if ($Phase -like 'user-*') { $out = "$root\results\user-startup" }
    $binary = if ($Phase -like 'user-*') { "$root\bundle\desktop\SPBackup.Desktop.exe" } else { "$root\bundle\agent\SPBackup.Agent.exe" }
    & "$root\bundle\probe\WindowsAgentServiceSmoke.exe" $binary $out $mode *> "$root\results\$Phase.log"
    $code = $LASTEXITCODE
} catch {
    $_ | Out-String | Add-Content "$root\results\$Phase.log"
    $code = 1
}
[IO.File]::WriteAllText("$root\results\$Phase-exit.txt", [string]$code)
exit $code
