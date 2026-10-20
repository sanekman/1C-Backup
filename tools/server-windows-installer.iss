#ifndef SourceFolder
  #error SourceFolder is required
#endif
#ifndef OutputFolder
  #error OutputFolder is required
#endif
#ifndef AppVersion
  #error AppVersion is required
#endif
[Setup]
AppId={{ACDE2073-28A8-43F8-9A84-BB0BE3C6831D}
AppName=1C-Backup Server
AppVersion={#AppVersion}
AppPublisher=1C-Backup
DefaultDirName={autopf}\1C-Backup Server
DefaultGroupName=1C-Backup Server
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.14393
OutputDir={#OutputFolder}
OutputBaseFilename=1C-Backup-Server-{#AppVersion}-windows-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
SetupLogging=yes
[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
[Files]
Source: "{#SourceFolder}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{commonprograms}\1C-Backup Server\Открыть панель"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\windows\Open-Panel.ps1"""
[Code]
var
  Credentials: TInputQueryWizardPage;
  PasswordFile, NameFile, AddressFile: String;
  Configured: Boolean;

function ExistingConfiguration: Boolean;
begin
  Result := FileExists(ExpandConstant('{commonappdata}\OneCBackupServer\server.json'));
end;

procedure InitializeWizard;
begin
  Credentials := CreateInputQueryPage(wpSelectDir, 'Первый запуск сервера', 'Название системы мониторинга и адрес подключения',
    'Имя помогает различать системы мониторинга. Адрес — полный HTTPS-адрес, доступный агентам. PostgreSQL и службы настраиваются автоматически. Логин: admin.');
  Credentials.Add('Имя сервера (например, Бухгалтерия — основной):', False);
  Credentials.Add('Адрес сервера (например, https://backup.example:7443/):', False);
  Credentials.Add('Пароль администратора (не менее 12 символов):', True);
  Credentials.Add('Повторите пароль:', True);
  Credentials.Values[0] := ExpandConstant('{param:SERVERNAME|}');
  if Credentials.Values[0] = '' then Credentials.Values[0] := '1C-Backup Server';
  Credentials.Values[1] := ExpandConstant('{param:SERVERADDRESS|}');
  if Credentials.Values[1] = '' then Credentials.Values[1] := 'https://' + GetComputerNameString + ':7443/';
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = Credentials.ID) and (ExistingConfiguration or WizardSilent);
end;

function ValidDisplayName(Value: String): Boolean;
var I: Integer;
begin
  Result := (Length(Trim(Value)) > 0) and (Length(Value) <= 128);
  for I := 1 to Length(Value) do
    if (Ord(Value[I]) < 32) or (Ord(Value[I]) = 127) then Result := False;
end;

function ValidServerAddress(Value: String): Boolean;
var I: Integer;
begin
  Result := (CompareText(Copy(Value, 1, 8), 'https://') = 0) and (Length(Value) > 8);
  for I := 1 to Length(Value) do
    if (Ord(Value[I]) <= 32) or (Pos(Value[I], '@?#\"') > 0) then Result := False;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = Credentials.ID) and not ExistingConfiguration and not WizardSilent then begin
    Result := (Length(Credentials.Values[2]) >= 12) and (Length(Credentials.Values[2]) <= 256) and
      (Credentials.Values[2] = Credentials.Values[3]) and ValidDisplayName(Credentials.Values[0]) and ValidServerAddress(Credentials.Values[1]);
    if not Result then MsgBox('Укажите понятное имя, полный HTTPS-адрес без логина и параметров, а также одинаковый пароль от 12 до 256 символов.', mbError, MB_OK);
  end;
end;

function RunScript(Path, Arguments: String): Boolean;
var ExitCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + Path + '" ' + Arguments,
    '', SW_HIDE, ewWaitUntilTerminated, ExitCode) and (ExitCode = 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if FileExists(ExpandConstant('{app}\windows\Stop-Server.ps1')) then
    if not RunScript(ExpandConstant('{app}\windows\Stop-Server.ps1'), '') then
      Result := 'Не удалось остановить сервер. Установка не будет заменять работающие файлы.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Args: String; ExitCode: Integer;
begin
  if CurStep = ssPostInstall then begin
    if not Exec(ExpandConstant('{app}\vc_redist.x64.exe'), '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
      RaiseException('Не удалось запустить установку Microsoft Visual C++.');
    if (ExitCode <> 0) and (ExitCode <> 3010) and (ExitCode <> 1638) then
      RaiseException('Не удалось установить Microsoft Visual C++. Код: ' + IntToStr(ExitCode));
    PasswordFile := ExpandConstant('{param:ADMINPASSWORDFILE|}');
    if not ExistingConfiguration and (PasswordFile = '') then begin
      if WizardSilent then RaiseException('Для первой тихой установки задайте /ADMINPASSWORDFILE=<защищённый файл>.');
      PasswordFile := ExpandConstant('{tmp}\administrator-password.txt');
      if not SaveStringToFile(PasswordFile, UTF8Encode(Credentials.Values[2]), False) then RaiseException('Не удалось подготовить пароль.');
    end;
    NameFile := ExpandConstant('{tmp}\server-display-name.txt');
    AddressFile := ExpandConstant('{tmp}\server-public-address.txt');
    if not SaveStringToFile(NameFile, UTF8Encode(Credentials.Values[0]), False) then RaiseException('Не удалось сохранить имя сервера.');
    if not SaveStringToFile(AddressFile, UTF8Encode(Credentials.Values[1]), False) then RaiseException('Не удалось сохранить адрес сервера.');
    Args := '-InstallDirectory "' + ExpandConstant('{app}') + '" -ServerNameFile "' + NameFile + '" -ServerAddressFile "' + AddressFile + '"';
    if PasswordFile <> '' then Args := Args + ' -AdministratorPasswordFile "' + PasswordFile + '"';
    try
      Configured := RunScript(ExpandConstant('{app}\windows\Configure-Server.ps1'), Args);
      if not Configured then RaiseException('Настройка сервера не завершена. Данные сохранены. Повторите установку; диагностика находится в ProgramData\OneCBackupServer\data\logs.');
    finally
      if PasswordFile = ExpandConstant('{tmp}\administrator-password.txt') then DeleteFile(PasswordFile);
      DeleteFile(NameFile); DeleteFile(AddressFile);
      Credentials.Values[2] := ''; Credentials.Values[3] := '';
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    if not RunScript(ExpandConstant('{app}\windows\Remove-Server.ps1'), '') then
      RaiseException('Не удалось остановить и удалить службы. Файлы сервера сохранены.');
end;
