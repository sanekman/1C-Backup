[CmdletBinding()]
param([Parameter(Mandatory)][string]$AgentExecutable,[Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$root=Join-Path ([IO.Path]::GetTempPath()) ('sp-onec-login-guard-'+[Guid]::NewGuid().ToString('N'))
$utf8=New-Object Text.UTF8Encoding $false
$report=[ordered]@{success=$false;scope='login-probe-controller-only';actualOneC=$false;checks=@();errorType=$null}
New-Item -ItemType Directory -Path $root | Out-Null
try {
 $platform=Join-Path $root 'owned-platform.exe'
 Add-Type -OutputAssembly $platform -OutputType ConsoleApplication -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
[assembly:System.Reflection.AssemblyFileVersion("8.5.1.1150")]
public static class OwnedOneCLoginProbe {
 static string Value(string text,string flag) {return Regex.Match(text,Regex.Escape(flag)+" \"([^\"]*)\"").Groups[1].Value;}
 public static int Main(string[] args) {
  if(args.Length!=2 || args[0]!="/@")return 11;
  byte[] bytes=File.ReadAllBytes(args[1]);
  bool utf8bom=bytes.Length>=3 && bytes[0]==239 && bytes[1]==187 && bytes[2]==191;
  string text=File.ReadAllText(args[1],Encoding.UTF8);
  string log=Value(text,"/Out");string target=Value(text,"/DumpCfg");string source=Value(text,"/F");
  if(log.Length==0 || target.Length==0 || source.Length==0)return 12;
  if(!File.Exists(Path.Combine(source,"1Cv8.1CD")))return 13;
  if(!text.StartsWith("DESIGNER ") || !text.Contains(" /WA- ") || !text.Contains("/P \"owned-login-probe-fixture\""))return 14;
  if(utf8bom){File.WriteAllText(target,"controlled configuration fixture, not real 1C");return 0;}
  File.WriteAllText(log,"Пользователь ИБ не идентифицирован",new UTF8Encoding(false));return 1;
 }
}
'@
 $workspace=Join-Path $root 'work'
 $testRoot=Join-Path $workspace 'onec-owned'
 $source=Join-Path $testRoot 'base'
 New-Item -ItemType Directory -Path $source -Force | Out-Null
 [IO.File]::WriteAllText((Join-Path $source '1Cv8.1CD'),'Owned clone fixture; not real 1C.',$utf8)
 $before=(Get-FileHash -LiteralPath (Join-Path $source '1Cv8.1CD')).Hash
 [IO.File]::WriteAllText((Join-Path $testRoot 'owned-test.json'),'{"format":1,"owner":"SPBackup-OneC-acceptance"}',$utf8)
 $version=[Diagnostics.FileVersionInfo]::GetVersionInfo($AgentExecutable).ProductVersion
 [IO.File]::WriteAllText((Join-Path $testRoot 'one-c-acceptance-result.json'),(ConvertTo-Json @{platformVersion='8.5.1.1150';agentVersion=$version}),$utf8)
 $profile=Join-Path $testRoot 'profiles\platform-dump';New-Item -ItemType Directory -Path $profile -Force | Out-Null
 $job=@{Kind='OneCFile';OneCMethod='PlatformDump';Source=$source;OneC=@{Username='Тестовый пользователь ИБ';ExecutablePath=$platform}}
 [IO.File]::WriteAllText((Join-Path $profile 'jobs.json'),(ConvertTo-Json -InputObject @($job) -Depth 8),$utf8)
 $scriptPath=Join-Path $repo 'tools/test_windows_onec_login.ps1'
 function Quote-Literal([string]$v){return "'"+$v.Replace("'","''")+"'"}
 $command="function global:Read-Host {param([string]`$Prompt,[switch]`$AsSecureString) if(-not `$AsSecureString){throw 'Unexpected interactive prompt'}; ConvertTo-SecureString 'owned-login-probe-fixture' -AsPlainText -Force }; & "+(Quote-Literal $scriptPath)+' -WorkParent '+(Quote-Literal $workspace)+' -AgentExecutable '+(Quote-Literal $AgentExecutable)
 $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
 & (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -EncodedCommand $encoded
 if($LASTEXITCODE -ne 0){throw 'Controlled login probe failed.'}
 $outputFile=@(Get-ChildItem -LiteralPath $testRoot -Filter 'one-c-login-probe-result.json' -Recurse) | Select-Object -First 1
 if(-not $outputFile){throw 'Login probe did not write its result.'}
 $result=Get-Content -LiteralPath $outputFile.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
 if(-not $result.success -or -not $result.vaultRoundTrip -or -not $result.cloneReused -or $result.originalDatabaseAccessed){throw 'Login probe did not confirm bounded credential round-trip.'}
 if(@($result.cases).Count -ne 4 -or @($result.cases | Where-Object {$_.passed}).Count -ne 1 -or -not ($result.cases | Where-Object {$_.encoding -eq 'utf8-bom'}).passed){throw 'Login probe misclassified the controlled encoding matrix.'}
 if((Get-FileHash -LiteralPath (Join-Path $source '1Cv8.1CD')).Hash -ne $before){throw 'Probe changed the controlled source bytes.'}
 if(@(Get-ChildItem -LiteralPath $testRoot -Recurse -Include 'startup.prm','probe.cf').Count -ne 0){throw 'Private parameters or generated configuration were left behind.'}
 $report.checks=@('real-agent-vault-round-trip','four-encoding-classifications-with-controlled-platform','owned-clone-reused-without-source-copy','source-bytes-preserved','private-parameter-and-configuration-cleanup')
 $report.success=$true
} catch {$report.errorType=$_.Exception.GetType().FullName;Write-Warning $_.Exception.Message}
finally {
 Remove-Item -LiteralPath $root -Recurse -Force
 New-Item -ItemType Directory -Path (Split-Path $Output -Parent) -Force | Out-Null
 [IO.File]::WriteAllText($Output,($report | ConvertTo-Json -Depth 5),$utf8)
}
if(-not $report.success){exit 1}
exit 0
