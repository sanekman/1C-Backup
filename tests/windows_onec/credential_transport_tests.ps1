[CmdletBinding()]
param([Parameter(Mandatory)][string]$AgentExecutable,[Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$errors=$null;$tokens=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'tools/test_windows_onec.ps1'),[ref]$tokens,[ref]$errors)
if($errors){throw ($errors | Out-String)}
$function=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Save-OneCTestCredential'},$true)
if(-not $function){throw 'Missing production credential transport function.'}
. ([scriptblock]::Create($function.Extent.Text))
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class OwnedCredentialProbe {
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
$report=[ordered]@{success=$false;scope='real-windows-powershell-credential-transport';actualOneC=$false;powershell=$PSVersionTable.PSVersion.ToString();checks=@();errorType=$null}
$original=[Console]::InputEncoding
$key=[Guid]::NewGuid().ToString('N')
try {
 foreach($case in @(
  @{encoding=(New-Object Text.UTF8Encoding $true);username='Owned ASCII user';password='owned fixture password'},
  @{encoding=(New-Object Text.UTF8Encoding $true);username='Тестовый пользователь ИБ';password='Owned пароль " /WA+ \\ fixture'},
  @{encoding=(New-Object Text.UTF8Encoding $false);username='Тестовый пользователь ИБ';password=''},
  @{encoding=[Text.Encoding]::GetEncoding(866);username='Тестовый пользователь ИБ';password='owned OEM host fixture'}
 )) {
  [Console]::InputEncoding=$case.encoding
  $encodingBefore=[Console]::InputEncoding
  $json=ConvertTo-Json @{username=$case.username;password=$case.password} -Compress
  $bytes=(New-Object Text.UTF8Encoding $false).GetBytes($json)
  try {$exitCode=Save-OneCTestCredential $key $bytes} finally {[Array]::Clear($bytes,0,$bytes.Length)}
  if($exitCode -ne 0){throw ('Credential helper failed with exit code '+$exitCode)}
  if([Console]::InputEncoding.CodePage -ne $encodingBefore.CodePage -or [Convert]::ToBase64String([Console]::InputEncoding.GetPreamble()) -ne [Convert]::ToBase64String($encodingBefore.GetPreamble())){throw 'Caller console encoding was not restored.'}
  $pointer=[IntPtr]::Zero
  if(-not [OwnedCredentialProbe]::Read(('OneCBackup/OneCInfobase/'+$key),1,0,[ref]$pointer)){throw 'Owned credential not found in Windows Vault.'}
  try {
   $credential=[Runtime.InteropServices.Marshal]::PtrToStructure($pointer,[type][OwnedCredentialProbe+Credential])
   $name=[Runtime.InteropServices.Marshal]::PtrToStringUni($credential.Username)
   $stored=New-Object byte[] $credential.BlobSize
   try {
    if($stored.Length){[Runtime.InteropServices.Marshal]::Copy($credential.Blob,$stored,0,$stored.Length)}
    if($name -cne $case.username -or [Text.Encoding]::Unicode.GetString($stored) -cne $case.password){throw 'Stored Unicode credentials differ from the fixture.'}
   } finally {[Array]::Clear($stored,0,$stored.Length)}
  } finally {[OwnedCredentialProbe]::CredFree($pointer)}
  $report.checks+=('exact-vault-values-with-'+$case.encoding.WebName+'-host-and-password-length-'+$case.password.Length)
 }
 $report.success=$true
} catch { $report.errorType=$_.Exception.GetType().FullName;Write-Warning $_.Exception.Message }
finally {
 [Console]::InputEncoding=$original
 & $AgentExecutable '--remove-onec-credentials' $key | Out-Null
 if($LASTEXITCODE -ne 0){$report.success=$false;$report.errorType='OwnedCredentialCleanupFailed'}
 $directory=Split-Path $Output -Parent
 New-Item -ItemType Directory -Path $directory -Force | Out-Null
 [IO.File]::WriteAllText($Output,($report | ConvertTo-Json -Depth 5),(New-Object Text.UTF8Encoding $false))
}
if(-not $report.success){exit 1}
exit 0
