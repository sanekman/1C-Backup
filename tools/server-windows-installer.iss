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
  Credentials, Network: TInputQueryWizardPage;
  OpenFirewall: TNewCheckBox;
  AddressPreview, NetworkHint: TNewStaticText;
  FirewallParameter: String;
  PasswordFile, NameFile, AddressFile: String;
  Configured: Boolean;

function ExistingConfiguration: Boolean;
begin
  Result := FileExists(ExpandConstant('{commonappdata}\OneCBackupServer\server.json'));
end;

function AddressAuthority(Value: String): String;
var Rest: String; I: Integer;
begin
  Rest := Copy(Value, 9, Length(Value));
  I := Pos('/', Rest);
  if I > 0 then Result := Copy(Rest, 1, I - 1) else Result := Rest;
end;

function AddressPort(Value: String): String;
var Authority: String; I: Integer;
begin
  Authority := AddressAuthority(Value);
  if Copy(Authority, 1, 1) = '[' then begin
    I := Pos(']', Authority);
    if (I > 0) and (Copy(Authority, I + 1, 1) = ':') then
      Result := Copy(Authority, I + 2, Length(Authority)) else Result := '443';
  end else begin
    I := Pos(':', Authority);
    if I > 0 then Result := Copy(Authority, I + 1, Length(Authority)) else Result := '443';
  end;
end;

function AddressWithPort(Value, Port: String): String;
var Authority, Host, Rest, Path: String; I: Integer;
begin
  Authority := AddressAuthority(Value); Host := Authority;
  if Copy(Authority, 1, 1) = '[' then begin
    I := Pos(']', Authority); if I > 0 then Host := Copy(Authority, 1, I);
  end else begin
    I := Pos(':', Authority); if I > 0 then Host := Copy(Authority, 1, I - 1);
  end;
  Rest := Copy(Value, 9, Length(Value)); I := Pos('/', Rest);
  if I > 0 then Path := Copy(Rest, I, Length(Rest)) else Path := '/';
  if Copy(Path, Length(Path), 1) <> '/' then Path := Path + '/';
  if Port = '443' then Result := 'https://' + Host + Path
  else Result := 'https://' + Host + ':' + Port + Path;
end;

function ValidPort(Value: String): Boolean;
var I, Number: Integer;
begin
  Result := Length(Value) > 0;
  for I := 1 to Length(Value) do
    if (Value[I] < '0') or (Value[I] > '9') then Result := False;
  Number := StrToIntDef(Value, -1);
  Result := Result and (Number >= 1) and (Number <= 65535) and (Number <> 15433);
end;

procedure PortChanged(Sender: TObject);
begin
  if AddressPreview <> nil then begin
    if ValidPort(Network.Values[0]) then
      AddressPreview.Caption := 'Адрес подключения: ' + AddressWithPort(Credentials.Values[1], IntToStr(StrToIntDef(Network.Values[0], -1)))
    else AddressPreview.Caption := 'Укажите допустимый HTTPS-порт от 1 до 65535, кроме 15433.';
  end;
end;

procedure InitializeWizard;
var PortParameter: String;
begin
  Credentials := CreateInputQueryPage(wpSelectDir, 'Первый запуск сервера', 'Название системы мониторинга и адрес подключения',
    'Имя помогает различать системы мониторинга. Укажите HTTPS-адрес; порт выбирается на следующем шаге. PostgreSQL и службы настраиваются автоматически. Логин: admin.');
  Credentials.Add('Имя сервера (например, Бухгалтерия — основной):', False);
  Credentials.Add('Адрес сервера (например, https://backup.example/):', False);
  Credentials.Add('Пароль администратора (не менее 12 символов):', True);
  Credentials.Add('Повторите пароль:', True);
  Credentials.Values[0] := ExpandConstant('{param:SERVERNAME|}');
  if Credentials.Values[0] = '' then Credentials.Values[0] := '1C-Backup Server';
  Credentials.Values[1] := ExpandConstant('{param:SERVERADDRESS|}');
  PortParameter := ExpandConstant('{param:HTTPSPORT|}');
  if Credentials.Values[1] = '' then begin
    Credentials.Values[1] := 'https://' + GetComputerNameString + '/';
    if PortParameter = '' then PortParameter := '7443';
  end else if PortParameter = '' then PortParameter := AddressPort(Credentials.Values[1]);
  Network := CreateInputQueryPage(Credentials.ID, 'Подключение к серверу', 'HTTPS-порт и доступ из сети',
    'Выберите порт для панели и агентов. Порт 7443 предложен по умолчанию; 15433 занят внутренней базой данных.');
  Network.Add('HTTPS-порт (1–65535):', False);
  Network.Values[0] := PortParameter;
  Network.Edits[0].OnChange := @PortChanged;
  OpenFirewall := TNewCheckBox.Create(Network);
  OpenFirewall.Parent := Network.Surface;
  OpenFirewall.Left := 0; OpenFirewall.Top := Network.Edits[0].Top + ScaleY(36);
  OpenFirewall.Width := Network.SurfaceWidth; OpenFirewall.Height := ScaleY(20);
  OpenFirewall.Caption := 'Открыть HTTPS-порт в брандмауэре Windows';
  NetworkHint := TNewStaticText.Create(Network);
  NetworkHint.Parent := Network.Surface; NetworkHint.AutoSize := False;
  NetworkHint.Left := 0; NetworkHint.Top := OpenFirewall.Top + OpenFirewall.Height + ScaleY(6);
  NetworkHint.Width := Network.SurfaceWidth; NetworkHint.Height := ScaleY(36);
  NetworkHint.WordWrap := True;
  NetworkHint.Caption := 'Разрешение действует только для доменных и частных сетей. Для работы лишь на этом компьютере флажок можно снять.';
  FirewallParameter := ExpandConstant('{param:FIREWALL|}');
  if (FirewallParameter <> '') and (FirewallParameter <> '0') and (FirewallParameter <> '1') then
    RaiseException('Параметр /FIREWALL должен быть 0 или 1.');
  OpenFirewall.Checked := FirewallParameter <> '0';
  AddressPreview := TNewStaticText.Create(Network);
  AddressPreview.Parent := Network.Surface; AddressPreview.AutoSize := False;
  AddressPreview.Left := 0; AddressPreview.Top := NetworkHint.Top + NetworkHint.Height + ScaleY(8);
  AddressPreview.Width := Network.SurfaceWidth; AddressPreview.Height := ScaleY(44);
  AddressPreview.WordWrap := True;
  PortChanged(Network);
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = Network.ID then PortChanged(Network);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := ((PageID = Credentials.ID) or (PageID = Network.ID)) and (ExistingConfiguration or WizardSilent);
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
  if (CurPageID = Network.ID) and not ExistingConfiguration and not WizardSilent then begin
    Result := ValidPort(Network.Values[0]);
    if not Result then MsgBox('Укажите HTTPS-порт от 1 до 65535. Порт 15433 используется внутренней базой сервера.', mbError, MB_OK);
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
var Args, Mode: String; ExitCode: Integer;
begin
  if CurStep = ssPostInstall then begin
    if not ExistingConfiguration and not ValidPort(Network.Values[0]) then
      RaiseException('Укажите допустимый HTTPS-порт от 1 до 65535, кроме 15433.');
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
    if not ExistingConfiguration then Args := Args + ' -HttpsPort ' + IntToStr(StrToIntDef(Network.Values[0], -1));
    Mode := 'Preserve';
    if not ExistingConfiguration or (FirewallParameter <> '') then begin
      if OpenFirewall.Checked then Mode := 'Allow' else Mode := 'None';
    end;
    Args := Args + ' -FirewallMode ' + Mode;
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
