$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$secret = [IO.File]::ReadAllText('C:\SPBLab\control.secret')
$port = New-Object IO.Ports.SerialPort 'COM2',115200,'None',8,'One'
$port.Encoding = [Text.Encoding]::UTF8
$port.ReadTimeout = 1000
$port.WriteTimeout = 10000
$port.Open()
[IO.File]::WriteAllText('C:\SPBLab\control-ready.txt', [string]$PID)
try {
    $statusPort = New-Object IO.Ports.SerialPort 'COM1',115200,'None',8,'One'
    $statusPort.Open()
    $statusPort.WriteLine('SPBackup control channel ready')
    $statusPort.Close()
} catch { # During initial setup COM1 belongs to bootstrap, which reports readiness itself.
}
try {
    while ($true) {
        try { $line = $port.ReadLine() } catch [TimeoutException] { continue }
        $id = $null
        try {
            $request = $line | ConvertFrom-Json
            $id = $request.id
            if ($request.protocol -ne 1 -or $request.token -cne $secret) { throw 'Unauthorized private VM control request.' }
            $script = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($request.script))
            # This endpoint exists only on the owned hypervisor socket. Product commands
            # still run in separately registered owner tasks, never as this SYSTEM broker.
            $text = (& ([ScriptBlock]::Create($script)) | Out-String).Trim()
            $reply = @{ protocol = 1; id = $id; code = 0; output = $text; error = '' }
        } catch {
            $reply = @{ protocol = 1; id = $id; code = 1; output = ''; error = $_.Exception.Message }
        }
        try { $port.WriteLine(($reply | ConvertTo-Json -Compress -Depth 5)) }
        catch [TimeoutException] { # A timed-out/disconnected controller will retry with a new ID.
        }
    }
} finally { $port.Close() }
