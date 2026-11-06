param([ValidateSet('Backup','Verify','Restore')][string]$Action='Backup')
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSEdition -eq 'Desktop') { $env:PSModulePath=Join-Path $PSHOME 'Modules' }
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if (!(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -Verb RunAs -ArgumentList @('-NoProfile','-STA','-ExecutionPolicy','Bypass','-File',('"'+$PSCommandPath+'"'),'-Action',$Action)
    exit
}
Add-Type -AssemblyName System.Windows.Forms
$titles=@{Backup='Создать резервную копию сервера'; Verify='Проверить резервную копию'; Restore='Восстановить сервер'}
try {
    $explanation=if ($Action -eq 'Backup') { 'Выберите папку на локальном NTFS-диске. Сервер и его PostgreSQL ненадолго остановятся. Копия содержит базу, настройки, ключи и сертификат: храните её отдельно и не передавайте посторонним. Восстановление поддерживается на этом компьютере с тем же выпуском программы.' }
        elseif ($Action -eq 'Verify') { 'Выберите папку готовой копии со snapshot.json. Будут проверены контрольные суммы, компьютер и версия программы. Работа сервера не прерывается.' }
        else { 'Выберите папку готовой копии. Сервер остановится; текущее состояние останется в отдельной папке. После проверки восстановления сервер запустится. Поддерживается тот же компьютер и тот же выпуск программы; перенос на другой ПК пока не выполняется.' }
    if ([Windows.Forms.MessageBox]::Show($explanation,$titles[$Action],'OKCancel','Information') -ne 'OK') { exit }
    $picker=New-Object Windows.Forms.FolderBrowserDialog
    try {
        $picker.Description=if ($Action -eq 'Backup') { 'Где сохранить новую копию сервера?' } else { 'Выберите папку копии со snapshot.json' }
        if ($picker.ShowDialog() -ne 'OK') { exit }; $folder=$picker.SelectedPath
    } finally { $picker.Dispose() }
    if ($Action -eq 'Backup') { $folder=Join-Path $folder ('SP-Backup-Server-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)) }
    if ($Action -eq 'Restore' -and [Windows.Forms.MessageBox]::Show('Восстановить сервер и запустить его? Вернутся пароль панели, права и агенты на дату копии. Текущие данные будут сохранены отдельно.',$titles[$Action],'YesNo','Warning') -ne 'Yes') { exit }
    $result=& (Join-Path $PSScriptRoot 'Server-Snapshot.ps1') -Action $Action -BackupDirectory $folder
    $text='Готово. '+$result.Path
    if ($Action -eq 'Restore' -and $result.PreviousData) { $text+=' Предыдущие данные сохранены: '+$result.PreviousData }
    [void][Windows.Forms.MessageBox]::Show($text,$titles[$Action],'OK','Information')
} catch { [void][Windows.Forms.MessageBox]::Show($_.Exception.Message,$titles[$Action],'OK','Error'); exit 1 }
