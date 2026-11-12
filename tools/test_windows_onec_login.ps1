[CmdletBinding()]
param([string]$WorkParent='E:\SPBackup-Tests',[string]$AgentExecutable,[ValidateRange(30,600)][int]$CaseTimeoutSeconds=120)
$ErrorActionPreference='Stop'
if($env:OS -ne 'Windows_NT'){throw 'Нужен Windows PowerShell на компьютере с тестовой копией 1С.'}
$report=[ordered]@{format=1;scenario='real-windows-onec-login-probe';startedUtc=[DateTime]::UtcNow.ToString('o');success=$false;originalDatabaseAccessed=$false;cloneReused=$false;vaultRoundTrip=$false;platformVersion=$null;agentVersion=$null;cases=@();errorType=$null;errorCode=$null}
$ownsProbeRoot=$false;$probeRoot=$null;$key=$null;$password=$null;$login=$null;$process=$null
function Quote-OneCParameter([string]$Value){
 if($Value.IndexOfAny([char[]]"`r`n`0") -ge 0){throw 'Параметр содержит недопустимый управляющий символ.'}
 return '"'+$Value.Replace('"','""')+'"'
}
function Get-SafePlatformReason([string]$Path){
 if(-not (Test-Path -LiteralPath $Path)){return 'no-platform-log'}
 $bytes=[IO.File]::ReadAllBytes($Path)
 if($bytes.Length -gt 1MB){return 'platform-error-see-local-log'}
 foreach($encoding in @([Text.Encoding]::UTF8,[Text.Encoding]::Unicode,[Text.Encoding]::GetEncoding(1251))){
  $text=$encoding.GetString($bytes).ToLowerInvariant()
  if($text.Contains('пользователь иб не идентифицирован') -or $text.Contains('пользователь информационной базы не идентифицирован')){return 'infobase-user-not-identified'}
  if($text.Contains('не обнаружена лицензия') -or $text.Contains('не найдена лицензия')){return 'license-unavailable'}
 }
 return 'platform-error-see-local-log'
}
function Save-OneCTestCredential([string]$Key, [byte[]]$Bytes) {
    # .NET Framework constructs an auto-flushed StreamWriter for redirected stdin.
    # Its console encoding can emit a BOM before raw bytes are written.
    $previousInputEncoding = [Console]::InputEncoding
    $save = $null
    try {
        [Console]::InputEncoding = New-Object Text.UTF8Encoding $false
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = $AgentExecutable; $start.Arguments = '--store-onec-credentials ' + $Key
        $start.UseShellExecute = $false; $start.CreateNoWindow = $true
        $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
        $save = [Diagnostics.Process]::Start($start)
        $out = $save.StandardOutput.ReadToEndAsync(); $err = $save.StandardError.ReadToEndAsync()
        $inputPipe = $save.StandardInput.BaseStream
        try { $inputPipe.Write($Bytes,0,$Bytes.Length); $inputPipe.Flush() }
        finally { $inputPipe.Close() }
        if (-not $save.WaitForExit(10000)) { $save.Kill(); throw 'Сохранение данных входа 1С не завершилось.' }
        $out.GetAwaiter().GetResult() | Out-Null; $err.GetAwaiter().GetResult() | Out-Null
        return $save.ExitCode
    } finally {
        if ($save) {
            if (-not $save.HasExited) { $save.Kill(); $save.WaitForExit(10000) | Out-Null }
            $save.Dispose()
        }
        [Console]::InputEncoding = $previousInputEncoding
    }
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class OneCAuthProbeVault {
 [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
 public struct Credential {
  public uint Flags, Type; public IntPtr TargetName, Comment;
  public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
  public uint BlobSize; public IntPtr Blob; public uint Persist, AttributeCount;
  public IntPtr Attributes, TargetAlias, Username;
 }
 [DllImport("advapi32.dll", EntryPoint="CredReadW", CharSet=CharSet.Unicode, SetLastError=true)]
 [return:MarshalAs(UnmanagedType.Bool)] public static extern bool Read(string target,uint type,uint flags,out IntPtr value);
 [DllImport("advapi32.dll")] public static extern void CredFree(IntPtr value);
}
'@

try {
 $candidate=$null
 foreach($file in @(Get-ChildItem -Path (Join-Path $WorkParent 'onec-*\one-c-acceptance-result.json') -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)){
  $testRoot=$file.DirectoryName
  $markerPath=Join-Path $testRoot 'owned-test.json'
  $jobsPath=Join-Path $testRoot 'profiles\platform-dump\jobs.json'
  $basePath=Join-Path $testRoot 'base'
  if(-not (Test-Path -LiteralPath $markerPath) -or -not (Test-Path -LiteralPath $jobsPath) -or -not (Test-Path -LiteralPath (Join-Path $basePath '1Cv8.1CD'))){continue}
  if((Get-Item -LiteralPath $testRoot).Attributes -band [IO.FileAttributes]::ReparsePoint){continue}
  if((Get-Item -LiteralPath $basePath).Attributes -band [IO.FileAttributes]::ReparsePoint){continue}
  if((Get-Item -LiteralPath (Join-Path $basePath '1Cv8.1CD')).Attributes -band [IO.FileAttributes]::ReparsePoint){continue}
  $marker=Get-Content -LiteralPath $markerPath -Raw -Encoding UTF8 | ConvertFrom-Json
  if($marker.format -ne 1 -or $marker.owner -ne 'SPBackup-OneC-acceptance'){continue}
  $jobs=@(Get-Content -LiteralPath $jobsPath -Raw -Encoding UTF8 | ConvertFrom-Json)
  $job=@($jobs | Where-Object {$_.Kind -eq 'OneCFile' -and $_.OneCMethod -eq 'PlatformDump' -and [IO.Path]::GetFullPath($_.Source).TrimEnd('\') -eq [IO.Path]::GetFullPath($basePath).TrimEnd('\')}) | Select-Object -First 1
  if(-not $job -or [string]::IsNullOrWhiteSpace([string]$job.OneC.Username)){continue}
  $candidate=[pscustomobject]@{root=$testRoot;source=$basePath;job=$job;report=(Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json)};break
 }
 if(-not $candidate){throw 'Не найдена принадлежащая тесту отдельная копия с заданием DT. Исходная база не открывалась.'}
 $platform=[string]$candidate.job.OneC.ExecutablePath
 if(-not (Test-Path -LiteralPath $platform -PathType Leaf)){throw 'Платформа из последней проверки не найдена.'}
 $info=[Diagnostics.FileVersionInfo]::GetVersionInfo($platform)
 $version="$($info.FileMajorPart).$($info.FileMinorPart).$($info.FileBuildPart).$($info.FilePrivatePart)"
 if($version -ne $candidate.report.platformVersion){throw 'Версия платформы изменилась.'}
 $report.platformVersion=$version
 if(-not $AgentExecutable){
  Add-Type -AssemblyName System.Windows.Forms
  $dialog=New-Object Windows.Forms.OpenFileDialog
  try {$dialog.Title='Выберите SPBackup.Agent.exe из тестовой сборки';$dialog.Filter='SPBackup.Agent.exe|SPBackup.Agent.exe';if($dialog.ShowDialog() -ne 'OK'){throw 'Выбор агента отменён.'};$AgentExecutable=$dialog.FileName} finally {$dialog.Dispose()}
 }
 $report.agentVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo($AgentExecutable).ProductVersion
 if($report.agentVersion -ne $candidate.report.agentVersion){throw 'Выбран агент другой сборки. Нужна та же тестовая сборка.'}
 $probeRoot=Join-Path $candidate.root ('login-probe-'+[Guid]::NewGuid().ToString('N'))
 New-Item -ItemType Directory -Path $probeRoot | Out-Null
 $ownsProbeRoot=$true
 $owner=[Security.Principal.WindowsIdentity]::GetCurrent().User
 $acl=New-Object Security.AccessControl.DirectorySecurity
 $acl.SetAccessRuleProtection($true,$false);$acl.SetOwner($owner)
 foreach($sid in @($owner.Value,'S-1-5-18','S-1-5-32-544') | Select-Object -Unique){$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule ((New-Object Security.Principal.SecurityIdentifier $sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow')))}
 Set-Acl -LiteralPath $probeRoot -AclObject $acl
 $closed=New-Object IO.FileStream (Join-Path $candidate.source '1Cv8.1CD'),'Open','Read','None';$closed.Dispose()
 $report.cloneReused=$true
 $username=[string]$candidate.job.OneC.Username
 Write-Host ('Пользователь ИБ из последнего задания: '+$username)
 Write-Host 'Используется готовая ТЕСТОВАЯ копия. Новая полная копия и чтение рабочей базы не выполняются.'
 $login=[Management.Automation.PSCredential]::new($username,(Read-Host 'Пароль пользователя ИБ (ввод скрыт)' -AsSecureString))
 $password=$login.GetNetworkCredential().Password
 $key=[Guid]::NewGuid().ToString('N')
 $bytes=(New-Object Text.UTF8Encoding $false).GetBytes((ConvertTo-Json @{username=$username;password=$password} -Compress))
 try {$exitCode=Save-OneCTestCredential $key $bytes} finally {[Array]::Clear($bytes,0,$bytes.Length)}
 if($exitCode -ne 0){$report.errorCode='CredentialHelperRejected';throw 'Помощник не сохранил тестовую запись.'}
 $pointer=[IntPtr]::Zero
 if(-not [OneCAuthProbeVault]::Read(('OneCBackup/OneCInfobase/'+$key),1,0,[ref]$pointer)){throw 'Тестовая запись не найдена в Windows Vault.'}
 try {
  $credential=[Runtime.InteropServices.Marshal]::PtrToStructure($pointer,[type][OneCAuthProbeVault+Credential])
  $storedName=[Runtime.InteropServices.Marshal]::PtrToStringUni($credential.Username)
  $storedBytes=New-Object byte[] $credential.BlobSize
  try {if($storedBytes.Length){[Runtime.InteropServices.Marshal]::Copy($credential.Blob,$storedBytes,0,$storedBytes.Length)};$storedPassword=[Text.Encoding]::Unicode.GetString($storedBytes)} finally {[Array]::Clear($storedBytes,0,$storedBytes.Length)}
 } finally {[OneCAuthProbeVault]::CredFree($pointer)}
 if($storedName -cne $username -or $storedPassword -cne $password){$report.errorCode='CredentialValuesChanged';throw 'Windows Vault вернул отличающиеся данные. Платформа не запускалась.'}
 $report.vaultRoundTrip=$true
 Write-Host 'Windows Vault: имя и пароль совпали посимвольно. Секреты в отчёт не включаются.'
 $ansi=[Text.Encoding]::GetEncoding([Text.Encoding]::Default.CodePage,[Text.EncoderFallback]::ExceptionFallback,[Text.DecoderFallback]::ExceptionFallback)
 $formats=@(@{name='windows-ansi';encoding=$ansi},@{name='utf8-bom';encoding=(New-Object Text.UTF8Encoding $true,$true)},@{name='utf8-no-bom';encoding=(New-Object Text.UTF8Encoding $false,$true)},@{name='utf16le-bom';encoding=(New-Object Text.UnicodeEncoding $false,$true,$true)})
 $report.windowsAnsiCodePage=$ansi.CodePage
 foreach($format in $formats){
  $caseRoot=Join-Path $probeRoot $format.name;New-Item -ItemType Directory -Path $caseRoot | Out-Null
  $parameter=Join-Path $caseRoot 'startup.prm';$configuration=Join-Path $caseRoot 'probe.cf';$log=Join-Path $caseRoot 'platform.log'
  $entry=[ordered]@{encoding=$format.name;passed=$false;exitCode=$null;reason=$null}
  try {
   $text='DESIGNER /F '+(Quote-OneCParameter $candidate.source)+' /DumpCfg '+(Quote-OneCParameter $configuration)+' /Out '+(Quote-OneCParameter $log)+' /DisableStartupMessages /DisableStartupDialogs /WA- /N '+(Quote-OneCParameter $storedName)+' /P '+(Quote-OneCParameter $storedPassword)
   try {[IO.File]::WriteAllText($parameter,$text,$format.encoding)} catch {if($_.Exception.GetBaseException() -is [Text.EncoderFallbackException]){$entry.reason='encoding-cannot-represent-parameters';continue};throw}
   $start=New-Object Diagnostics.ProcessStartInfo
   $start.FileName=$platform;$start.Arguments='/@ "'+$parameter+'"';$start.WorkingDirectory=$caseRoot;$start.UseShellExecute=$false;$start.CreateNoWindow=$true
   $process=[Diagnostics.Process]::Start($start)
   if(-not $process.WaitForExit($CaseTimeoutSeconds*1000)){$entry.reason='timeout';$process.Kill();$process.WaitForExit(30000) | Out-Null;throw 'Проверка входа превысила срок; следующие варианты не запускаются.'}
   $entry.exitCode=$process.ExitCode
   $entry.passed=$process.ExitCode -eq 0 -and (Test-Path -LiteralPath $configuration) -and (Get-Item -LiteralPath $configuration).Length -gt 0
   if($entry.passed){$entry.reason='configuration-export-created';$report.success=$true}else{$entry.reason=Get-SafePlatformReason $log}
  } finally {
   if($process){if(-not $process.HasExited){$process.Kill();$process.WaitForExit(30000) | Out-Null};$process.Dispose();$process=$null}
   if(Test-Path -LiteralPath $parameter){Remove-Item -LiteralPath $parameter -Force}
   if(Test-Path -LiteralPath $configuration){Remove-Item -LiteralPath $configuration -Force}
   $report.cases+=,$entry
   Write-Host ($format.name+': '+$entry.reason)
  }
 }
} catch {$report.success=$false;$report.errorType=$_.Exception.GetType().FullName;Write-Warning $_.Exception.Message}
finally {
 if($process){if(-not $process.HasExited){$process.Kill();$process.WaitForExit(30000) | Out-Null};$process.Dispose()}
 if($key){& $AgentExecutable '--remove-onec-credentials' $key | Out-Null;if($LASTEXITCODE -ne 0){$report.success=$false;$report.errorCode='CredentialCleanupFailed'}}
 if($login){$login.Password.Dispose()};$password=$null;$storedPassword=$null;$text=$null
 $report.finishedUtc=[DateTime]::UtcNow.ToString('o')
 if($ownsProbeRoot){$output=Join-Path $probeRoot 'one-c-login-probe-result.json';[IO.File]::WriteAllText($output,($report | ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding $false));Write-Host ('Отчёт: '+$output);Write-Host 'Передавайте только JSON-отчёт, без журналов и файлов базы.'}
}
if(-not $report.success){exit 1}
exit 0
