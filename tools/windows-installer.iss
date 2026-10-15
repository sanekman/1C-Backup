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
SetupIconFile=../src/OneCBackup.Desktop/Assets/one-c-backup.ico
AppId={{142FA512-738F-4BE8-9C31-487B3500B30B}
AppName=1C-Backup
AppVersion={#AppVersion}
AppPublisher=1C-Backup
DefaultDirName={autopf}\1C-Backup
DefaultGroupName=1C-Backup
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.14393
OutputDir={#OutputFolder}
OutputBaseFilename=1C-Backup-{#AppVersion}-windows-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\OneCBackup.Desktop.exe
CloseApplications=no
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; Flags: unchecked

[Files]
Source: "{#SourceFolder}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{commonprograms}\1C-Backup\1C-Backup"; Filename: "{app}\OneCBackup.Desktop.exe"
Name: "{commondesktop}\1C-Backup"; Filename: "{app}\OneCBackup.Desktop.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\OneCBackup.Desktop.exe"; Description: "Открыть 1C-Backup"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunValue = 'OneCBackupAgent';

function InitializeSetup: Boolean;
var
  Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  { MinVersion checks the Server 2016 floor; keep the client 22H2 floor. }
  Result := (Version.ProductType = VER_NT_SERVER) or
    (Version.ProductType = VER_NT_DOMAIN_CONTROLLER) or (Version.Build >= 19045);
  if not Result then
    SuppressibleMsgBox('Нужна Windows 10 22H2 или Windows 11. Для серверной ОС — Windows Server 2016 или новее.', mbError, MB_OK, IDOK);
end;

function ProfileAvailable: Boolean;
var
  Names: TArrayOfString;
  I: Integer;
  LockFile: String;
  Stream: TFileStream;
begin
  Result := True;
  Names := ['desktop.lock', 'engine.lock'];
  for I := 0 to GetArrayLength(Names) - 1 do begin
    LockFile := ExpandConstant('{localappdata}\OneCBackup\') + Names[I];
    if FileExists(LockFile) then begin
      try
        Stream := TFileStream.Create(LockFile, fmOpenReadWrite or fmShareExclusive);
        Stream.Free;
      except
        Result := False;
        Exit;
      end;
    end;
  end;
end;

function ProgramAvailable: Boolean;
var Names: TArrayOfString; I: Integer; Path: String; Stream: TFileStream;
begin
  Result := True;
  Names := ['OneCBackup.Desktop.exe', 'OneCBackup.Agent.exe', 'OneCBackup.Desktop.dll', 'OneCBackup.Agent.dll', 'OneCBackup.Core.dll'];
  for I := 0 to GetArrayLength(Names) - 1 do begin
    Path := ExpandConstant('{app}\') + Names[I];
    if FileExists(Path) then begin
      try
        Stream := TFileStream.Create(Path, fmOpenReadWrite or fmShareExclusive);
        Stream.Free;
      except
        Result := False;
        Exit;
      end;
    end;
  end;
end;

function ServiceImage: String;
begin
  Result := '';
  RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Services\OneCBackupAgent', 'ImagePath', Result);
end;

function ServiceAtCurrentLocation: Boolean;
var Prefix: String;
begin
  Prefix := '"' + ExpandConstant('{app}\OneCBackup.Agent.exe') + '" --service ';
  Result := CompareText(Copy(ServiceImage, 1, Length(Prefix)), Prefix) = 0;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if (ServiceImage <> '') and not ServiceAtCurrentLocation then begin
    Result := 'Служба зарегистрирована в другой папке. Перед переносом остановите и удалите службу в настройках прежней версии. Задания и копии сохранятся.';
    Exit;
  end;
  if not ProfileAvailable or not ProgramAvailable then
    Result := 'Выберите «Выйти полностью» в 1C-Backup и остановите службу, в том числе в других сеансах Windows. Файлы программы или профиль используются либо недоступны.';
end;

function InitializeUninstall: Boolean;
begin
  Result := False;
  if ServiceAtCurrentLocation then begin
    SuppressibleMsgBox('Перед удалением программы удалите службу в настройках 1C-Backup. Задания и копии сохранятся.', mbError, MB_OK, IDOK);
    Exit;
  end;
  Result := ProfileAvailable and ProgramAvailable;
  if not Result then
    SuppressibleMsgBox('Выберите «Выйти полностью» в 1C-Backup и остановите службу, в том числе в других сеансах Windows. Файлы программы или профиль используются либо недоступны.', mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  CurrentValue, InstalledValue, TrayValue: String;
begin
  if CurUninstallStep = usUninstall then begin
    InstalledValue := '"' + ExpandConstant('{app}\OneCBackup.Agent.exe') + '" --run --quiet';
    TrayValue := '"' + ExpandConstant('{app}\OneCBackup.Desktop.exe') + '" --tray --start-agent';
    if RegQueryStringValue(HKCU, RunKey, RunValue, CurrentValue) then
      if (CompareText(CurrentValue, InstalledValue) = 0) or (CompareText(CurrentValue, TrayValue) = 0) then
        RegDeleteValue(HKCU, RunKey, RunValue);
  end;
end;
