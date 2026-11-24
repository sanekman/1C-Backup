param([ValidateSet('Backup','Verify','Restore','PrepareUpgrade','VerifyUpgrade','RollbackUpgrade')][string]$Action='Backup')
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSEdition -eq 'Desktop') { $env:PSModulePath=Join-Path $PSHOME 'Modules' }
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if (!(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -Verb RunAs -ArgumentList @('-NoProfile','-STA','-ExecutionPolicy','Bypass','-File',('"'+$PSCommandPath+'"'),'-Action',$Action)
    exit
}
Add-Type -AssemblyName System.Windows.Forms
$titles=@{PrepareUpgrade='Подготовить обновление сервера'; VerifyUpgrade='Проверить комплект возврата'; RollbackUpgrade='Вернуть программу и данные'; Backup='Создать резервную копию сервера'; Verify='Проверить резервную копию'; Restore='Восстановить сервер'}
try {
    $explanation=if ($Action -in @('Backup','PrepareUpgrade')) { 'Выберите папку на локальном NTFS-диске. Сервер и его PostgreSQL ненадолго остановятся. Копия содержит базу, настройки, ключи и сертификат: храните её отдельно и не передавайте посторонним. Восстановление поддерживается на этом компьютере с тем же выпуском программы.' }
        elseif ($Action -in @('Verify','VerifyUpgrade')) { 'Выберите папку готовой копии со snapshot.json. Будут проверены контрольные суммы, компьютер и версия программы. Работа сервера не прерывается.' }
        else { 'Выберите папку готовой копии. Сервер остановится; текущее состояние останется в отдельной папке. После проверки восстановления сервер запустится. Поддерживается тот же компьютер и тот же выпуск программы; перенос на другой ПК пока не выполняется.' }
    if ($Action -in @('PrepareUpgrade','VerifyUpgrade','RollbackUpgrade')) { $explanation+=' Комплект обновления включает точные файлы программы и данные. Возврат заменит и программу, и данные на дату комплекта; совместимость между разными компьютерами не поддерживается.' }
    if ([Windows.Forms.MessageBox]::Show($explanation,$titles[$Action],'OKCancel','Information') -ne 'OK') { exit }
    $picker=New-Object Windows.Forms.FolderBrowserDialog
    try {
        $picker.Description=if ($Action -in @('Backup','PrepareUpgrade')) { 'Где сохранить новую копию сервера?' } else { 'Выберите папку копии со snapshot.json' }
        if ($picker.ShowDialog() -ne 'OK') { exit }; $folder=$picker.SelectedPath
    } finally { $picker.Dispose() }
    if ($Action -in @('Backup','PrepareUpgrade')) { $folder=Join-Path $folder ('SP-Backup-Server-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)) }
    if ($Action -in @('Restore','RollbackUpgrade') -and [Windows.Forms.MessageBox]::Show('Восстановить сервер и запустить его? Вернутся пароль панели, права и агенты на дату копии. Текущие данные будут сохранены отдельно.',$titles[$Action],'YesNo','Warning','Button2') -ne 'Yes') { exit }
    $result=& (Join-Path $PSScriptRoot 'Server-Snapshot.ps1') -Action $Action -BackupDirectory $folder
    $text='Готово. '+$result.Path
    if ($result.PSObject.Properties['CreatedUtc']) { $text+=' Дата копии (UTC): '+$result.CreatedUtc+'. Файлов данных: '+$result.Files+'.' }
    if ($Action -in @('Restore','RollbackUpgrade') -and $result.PreviousData) { $text+=' Предыдущие данные сохранены: '+$result.PreviousData }
    if ($result.PSObject.Properties['PreviousProgram'] -and $result.PreviousProgram) { $text+=' Предыдущая программа сохранена: '+$result.PreviousProgram }
    [void][Windows.Forms.MessageBox]::Show($text,$titles[$Action],'OK','Information')
} catch { [void][Windows.Forms.MessageBox]::Show($_.Exception.Message,$titles[$Action],'OK','Error'); exit 1 }
