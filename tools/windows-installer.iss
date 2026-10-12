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
DefaultDirName={localappdata}\Programs\1C-Backup
DefaultGroupName=1C-Backup
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
OutputDir={#OutputFolder}
OutputBaseFilename=1C-Backup-windows-x64-setup
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
Name: "{userprograms}\1C-Backup\1C-Backup"; Filename: "{app}\OneCBackup.Desktop.exe"
Name: "{userdesktop}\1C-Backup"; Filename: "{app}\OneCBackup.Desktop.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\OneCBackup.Desktop.exe"; Description: "Открыть 1C-Backup"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunValue = 'OneCBackupAgent';

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

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not ProfileAvailable then
    Result := 'Выберите «Выйти полностью» в трее или настройках 1C-Backup. В старой версии остановите автоматическое копирование и закройте окно перед установкой. Данные профиля сейчас используются или недоступны.';
end;

function InitializeUninstall: Boolean;
begin
  Result := ProfileAvailable;
  if not Result then
    SuppressibleMsgBox('Выберите «Выйти полностью» в трее или настройках 1C-Backup. В старой версии остановите автоматическое копирование и закройте окно перед удалением. Задания, история и пароли сохраняются.', mbError, MB_OK, IDOK);
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
