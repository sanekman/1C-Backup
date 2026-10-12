Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!include "FileFunc.nsh"

!ifndef SourceFolder
  !error "SourceFolder is required"
!endif
!ifndef OutputFolder
  !error "OutputFolder is required"
!endif
!ifndef AppVersion
  !error "AppVersion is required"
!endif
!ifndef UninstallFiles
  !error "UninstallFiles is required"
!endif

Icon "../src/OneCBackup.Desktop/Assets/one-c-backup.ico"
UninstallIcon "../src/OneCBackup.Desktop/Assets/one-c-backup.ico"
Name "1C-Backup"
OutFile "${OutputFolder}/1C-Backup-windows-x64-setup.exe"
InstallDir "$LOCALAPPDATA\Programs\1C-Backup"
RequestExecutionLevel user
SetCompressor /SOLID lzma
ShowInstDetails show
ShowUninstDetails show
VIProductVersion "0.1.0.0"
VIAddVersionKey "ProductName" "1C-Backup"
VIAddVersionKey "ProductVersion" "${AppVersion}"
VIAddVersionKey "FileVersion" "${AppVersion}"
VIAddVersionKey "FileDescription" "1C-Backup Windows x64 installer"
VIAddVersionKey "LegalCopyright" "1C-Backup"

!define UninstallKey "Software\Microsoft\Windows\CurrentVersion\Uninstall\{142FA512-738F-4BE8-9C31-487B3500B30B}_is1"
!define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\OneCBackup.Desktop.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Открыть 1C-Backup"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "Russian"

; Opening an existing lock with no sharing matches the desktop/agent lease.
; Missing files are allowed; sharing/access errors refuse installation and uninstall.
!macro CheckLock FileName
  StrCpy $0 "$LOCALAPPDATA\OneCBackup\${FileName}"
  System::Call 'kernel32::CreateFileW(w r0, i 0xC0000000, i 0, p 0, i 3, i 128, p 0) p .r1 ?e'
  Pop $2
  ${If} $1 = -1
    ${If} $2 != 2
    ${AndIf} $2 != 3
      MessageBox MB_OK|MB_ICONSTOP "Выберите «Выйти полностью» в трее или настройках 1C-Backup. В старой версии остановите автоматическое копирование и закройте окно. Рабочий профиль используется или недоступен. Задания и копии сохраняются." /SD IDOK
      SetErrorLevel 1
      Abort
    ${EndIf}
  ${Else}
    System::Call 'kernel32::CloseHandle(p r1) i'
  ${EndIf}
!macroend

!macro ProfileCheck Prefix
Function ${Prefix}ProfileAvailable
  Push $0
  Push $1
  Push $2
  !insertmacro CheckLock "desktop.lock"
  !insertmacro CheckLock "engine.lock"
  Pop $2
  Pop $1
  Pop $0
FunctionEnd
!macroend
!insertmacro ProfileCheck ""
!insertmacro ProfileCheck "un."

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "Нужна 64-разрядная Windows 10 22H2 или Windows 11." /SD IDOK
    SetErrorLevel 1
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_OK|MB_ICONSTOP "Нужна Windows 10 22H2 или Windows 11." /SD IDOK
    SetErrorLevel 1
    Abort
  ${EndIf}
  SetRegView 64
  ReadRegStr $0 HKLM "Software\Microsoft\Windows NT\CurrentVersion" "CurrentBuildNumber"
  ${If} $0 < 19045
    MessageBox MB_OK|MB_ICONSTOP "Нужна Windows 10 22H2 или Windows 11." /SD IDOK
    SetErrorLevel 1
    Abort
  ${EndIf}
  SetShellVarContext current
  Call ProfileAvailable
FunctionEnd

Function VerifyDirectory
  GetFullPathName $INSTDIR "$INSTDIR"
  ${GetRoot} "$INSTDIR" $0
  ${If} $INSTDIR == "$0"
  ${OrIf} $INSTDIR == "$0\"
    SetErrorLevel 1
    Abort
  ${EndIf}
  ; Never install directly into the backup profile or user-directory roots.
  ${If} $INSTDIR == "$LOCALAPPDATA\OneCBackup"
  ${OrIf} $INSTDIR == "$LOCALAPPDATA"
  ${OrIf} $INSTDIR == "$PROFILE"
    SetErrorLevel 1
    Abort
  ${EndIf}
FunctionEnd

Function .onVerifyInstDir
  Call VerifyDirectory
FunctionEnd

Function un.onInit
  SetRegView 64
  SetShellVarContext current
  Call un.ProfileAvailable
FunctionEnd

Section "Программа" MainSection
  SectionIn RO
  Call VerifyDirectory
  Call ProfileAvailable
  SetOutPath "$INSTDIR"
  File /r "${SourceFolder}/*"
  WriteUninstaller "$INSTDIR\unins000.exe"
  CreateDirectory "$SMPROGRAMS\1C-Backup"
  CreateShortCut "$SMPROGRAMS\1C-Backup\1C-Backup.lnk" "$INSTDIR\OneCBackup.Desktop.exe"
  WriteRegStr HKCU "${UninstallKey}" "DisplayName" "1C-Backup"
  WriteRegStr HKCU "${UninstallKey}" "DisplayVersion" "${AppVersion}"
  WriteRegStr HKCU "${UninstallKey}" "Publisher" "1C-Backup"
  WriteRegStr HKCU "${UninstallKey}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UninstallKey}" "DisplayIcon" "$INSTDIR\OneCBackup.Desktop.exe"
  WriteRegStr HKCU "${UninstallKey}" "UninstallString" '$\"$INSTDIR\unins000.exe$\"'
  WriteRegStr HKCU "${UninstallKey}" "QuietUninstallString" '$\"$INSTDIR\unins000.exe$\" /S'
  WriteRegDWORD HKCU "${UninstallKey}" "NoModify" 1
  WriteRegDWORD HKCU "${UninstallKey}" "NoRepair" 1
SectionEnd

Section "Ярлык на рабочем столе" DesktopSection
  CreateShortCut "$DESKTOP\1C-Backup.lnk" "$INSTDIR\OneCBackup.Desktop.exe"
SectionEnd

Section "Uninstall"
  Call un.ProfileAvailable
  ReadRegStr $0 HKCU "${RunKey}" "OneCBackupAgent"
  StrCpy $1 '$\"$INSTDIR\OneCBackup.Agent.exe$\" --run --quiet'
  StrCpy $2 '$\"$INSTDIR\OneCBackup.Desktop.exe$\" --tray --start-agent'
  ${If} $0 == $1
  ${OrIf} $0 == $2
    DeleteRegValue HKCU "${RunKey}" "OneCBackupAgent"
  ${EndIf}
  !include "${UninstallFiles}"
  Delete "$INSTDIR\unins000.exe"
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\1C-Backup\1C-Backup.lnk"
  RMDir "$SMPROGRAMS\1C-Backup"
  Delete "$DESKTOP\1C-Backup.lnk"
  DeleteRegKey HKCU "${UninstallKey}"
SectionEnd
