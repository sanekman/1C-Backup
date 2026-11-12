# Read-only local diagnostics, including when the web panel cannot open.
[CmdletBinding()]
param([string]$DataDirectory=(Join-Path $env:ProgramData 'OneCBackupServer'),[switch]$ShowWindow)
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSEdition -eq 'Desktop') { $env:PSModulePath=Join-Path $PSHOME 'Modules' }
if ($ShowWindow) {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    if (!(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -Verb RunAs -ArgumentList @('-NoProfile','-STA','-ExecutionPolicy','Bypass','-File',('"'+$PSCommandPath+'"'),'-DataDirectory',('"'+$DataDirectory+'"'),'-ShowWindow')
        exit
    }
}
$items=New-Object Collections.Generic.List[object]
function Item([string]$Code,[string]$State,[string]$Text,[string]$Action) { $items.Add([pscustomobject]@{Code=$Code;State=$State;Text=$Text;Action=$Action}) }
foreach ($name in @('OneCBackupServerDatabase','OneCBackupServer')) {
    try {
        $service=Get-Service -Name $name -ErrorAction Stop
        if ($service.Status -eq 'Running') { Item $name 'Healthy' ($name+': работает') '' }
        elseif ($service.Status -eq 'Stopped') { Item $name 'Error' ($name+': остановлена') 'Откройте services.msc. Сначала запустите OneCBackupServerDatabase, затем OneCBackupServer. Если запуск не проходит, проверьте место и setup.log; данные базы и ключи не удаляйте.' }
        else { Item $name 'Warning' ($name+': '+$service.Status) 'Дождитесь завершения перехода службы. Если состояние не меняется, сохраните диагностику Windows и журнал установки.' }
    } catch { Item $name 'Unknown' ($name+': состояние не получено') 'Проверьте наличие штатной установки и право чтения служб Windows. Запустите проверку от администратора.' }
}
$settings=$null
try { $settings=Get-Content -LiteralPath (Join-Path $DataDirectory 'server.json') -Raw -Encoding UTF8 | ConvertFrom-Json }
catch { Item 'Configuration' 'Unknown' 'Настройки сервера не прочитаны' 'Запустите проверку от администратора. Если server.json отсутствует, сохраните существующие данные и восстановите настройки из проверенной копии; не создавайте новую БД поверх прежней.' }
$diskPath=if ($settings -and $settings.Server.DataDirectory) { [string]$settings.Server.DataDirectory } else { $DataDirectory }
try {
    $drive=New-Object IO.DriveInfo([IO.Path]::GetPathRoot([IO.Path]::GetFullPath($diskPath)))
    $free=[long]$drive.AvailableFreeSpace
    if ($free -lt 268435456) { Item 'DataDisk' 'Error' ('Критически мало места: '+[Math]::Round($free/1MB)+' МиБ') 'Освободите место или увеличьте диск. Файлы PostgreSQL, ключи панели и сертификаты не удаляйте.' }
    elseif ($free -lt 1073741824) { Item 'DataDisk' 'Warning' ('Мало места: '+[Math]::Round($free/1MB)+' МиБ') 'Освободите место или увеличьте диск до следующего обновления или копирования.' }
    else { Item 'DataDisk' 'Healthy' ('Свободно на диске данных: '+[Math]::Round($free/1GB,1)+' ГиБ') '' }
} catch { Item 'DataDisk' 'Unknown' 'Свободное место не определено' 'Проверьте доступ к папке данных и состояние диска. Диск PostgreSQL при внешнем размещении проверьте отдельно.' }
if ($settings) {
    try {
        $listen=[Uri](([string]$settings.Urls) -replace '://[+*]:','://localhost:')
        $prefix=if ($settings.Server.PSObject.Properties['PathBase']) { [string]$settings.Server.PathBase } else { '' }
        $url=$listen.AbsoluteUri.TrimEnd('/')+$prefix+'/health/ready'
        [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
        $response=Invoke-WebRequest -UseBasicParsing -Uri $url -TimeoutSec 5
        if ($response.StatusCode -ne 200) { throw 'Not ready' }
        Item 'HttpsReadiness' 'Healthy' 'Локальный HTTPS: сервер и схема БД готовы' 'Для первого агента используйте публичный адрес и отпечаток из приглашения; эта проверка не подтверждает доступ с другого компьютера.'
    } catch { Item 'HttpsReadiness' 'Error' 'Готовность сервера по доверенному HTTPS не подтверждена' 'Проверьте обе службы, место, дату Windows и сертификат. После ошибки миграции повторите тот же установщик или выполните проверенный возврат полного комплекта. Проверку TLS не отключайте.' }
    try {
        $cer=Join-Path $DataDirectory 'server.cer'
        $certificate=New-Object Security.Cryptography.X509Certificates.X509Certificate2($cer)
        try {
            if ($certificate.NotBefore -gt (Get-Date)) { Item 'CertificateFile' 'Error' 'Сертификат на диске ещё не действует' 'Сверьте дату Windows и сертификат для адреса сервера.' }
            elseif ($certificate.NotAfter -le (Get-Date)) { Item 'CertificateFile' 'Error' 'Срок сертификата на диске истёк' 'Подготовьте действующий сертификат, перезапустите службу и обновите закреплённый отпечаток в агентах через доверенный канал.' }
            elseif ($certificate.NotAfter -le (Get-Date).AddDays(30)) { Item 'CertificateFile' 'Warning' 'Сертификат на диске истекает в ближайшие 30 дней' 'Подготовьте замену заранее. Панель показывает загруженный сертификат, который может отличаться от файла до перезапуска.' }
            else { Item 'CertificateFile' 'Healthy' 'Сертификат на диске находится в сроке действия' 'Это проверка server.cer штатной установки; фактически загруженный сертификат и внешний обратный прокси проверяются отдельно.' }
        } finally { $certificate.Dispose() }
    } catch { Item 'CertificateFile' 'Unknown' 'Срок сертификата на диске не получен' 'Для нестандартного сертификата проверьте его в настройках HTTPS. При работающей панели используйте сведения о загруженном сертификате.' }
}
$report=[pscustomobject]@{Format=1;CheckedUtc=[DateTime]::UtcNow.ToString('o');Healthy=(@($items | Where-Object State -ne 'Healthy').Count -eq 0);Checks=$items.ToArray()}
if ($ShowWindow) {
    Add-Type -AssemblyName System.Windows.Forms
    $form=New-Object Windows.Forms.Form; $form.Text='SP Backup Server — проверка на этом компьютере'; $form.Width=760;$form.Height=580;$form.StartPosition='CenterScreen'
    $text=New-Object Windows.Forms.TextBox;$text.Multiline=$true;$text.ReadOnly=$true;$text.ScrollBars='Vertical';$text.Dock='Fill';$text.Font=New-Object Drawing.Font('Segoe UI',10)
    $text.Text=(@($items | ForEach-Object { $_.Text+$(if ($_.Action) { "`r`n"+$_.Action } else { '' }) }) -join "`r`n`r`n")+"`r`n`r`nПроверка ничего не запускает и не меняет. Если панель недоступна, сохраните этот текст вместе с временем проблемы. Паролей и строк подключения здесь нет."
    $form.Controls.Add($text);[void]$form.ShowDialog();$form.Dispose()
} else { $report }
