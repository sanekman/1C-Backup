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

!define MUI_ICON "../src/OneCBackup.Desktop/Assets/one-c-backup.ico"
!define MUI_UNICON "../src/OneCBackup.Desktop/Assets/one-c-backup.ico"
Name "1C-Backup"
OutFile "${OutputFolder}/1C-Backup-${AppVersion}-windows-x64-setup.exe"
InstallDir "$PROGRAMFILES64\1C-Backup"
RequestExecutionLevel admin
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
Var InstallOwnerLocalData
Var InstallOwnerProfile
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_TEXT "1C-Backup установлен. Запустите программу из меню «Пуск»: задания и настройки будут открыты в профиле вашего пользователя."
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!define MUI_DIRECTORYPAGE_VERIFYONLEAVE
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
  StrCpy $0 "$InstallOwnerLocalData\OneCBackup\${FileName}"
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

!macro CheckProgramFile FileName
  StrCpy $0 "$INSTDIR\${FileName}"
  System::Call 'kernel32::CreateFileW(w r0, i 0xC0000000, i 0, p 0, i 3, i 128, p 0) p .r1 ?e'
  Pop $2
  ${If} $1 = -1
    ${If} $2 != 2
    ${AndIf} $2 != 3
      MessageBox MB_OK|MB_ICONSTOP "Файлы программы используются или недоступны. Закройте 1C-Backup через «Выйти полностью» и остановите службу, в том числе в других сеансах Windows." /SD IDOK
      SetErrorLevel 1
      Abort
    ${EndIf}
  ${Else}
    System::Call 'kernel32::CloseHandle(p r1) i'
  ${EndIf}
!macroend
!macro ProgramCheck Prefix
Function ${Prefix}ProgramAvailable
  Push $0
  Push $1
  Push $2
  !insertmacro CheckProgramFile "OneCBackup.Desktop.exe"
  !insertmacro CheckProgramFile "OneCBackup.Agent.exe"
  !insertmacro CheckProgramFile "OneCBackup.Desktop.dll"
  !insertmacro CheckProgramFile "OneCBackup.Agent.dll"
  !insertmacro CheckProgramFile "OneCBackup.Core.dll"
  Pop $2
  Pop $1
  Pop $0
FunctionEnd
!macroend
!insertmacro ProgramCheck ""
!insertmacro ProgramCheck "un."

Function VerifyServiceLocation
  Push $0
  Push $1
  Push $2
  ReadRegStr $0 HKLM "SYSTEM\CurrentControlSet\Services\OneCBackupAgent" "ImagePath"
  ${If} $0 != ""
    StrCpy $1 '"$INSTDIR\OneCBackup.Agent.exe" --service '
    StrLen $2 $1
    StrCpy $0 $0 $2
    ${If} $0 != $1
      MessageBox MB_OK|MB_ICONSTOP "Служба 1C-Backup зарегистрирована в другой папке. Перед переносом программы остановите и удалите службу в настройках прежней версии. Задания и копии сохранятся." /SD IDOK
      SetErrorLevel 1
      Abort
    ${EndIf}
  ${EndIf}
  Pop $2
  Pop $1
  Pop $0
FunctionEnd

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
  StrCpy $InstallOwnerLocalData "$LOCALAPPDATA"
  StrCpy $InstallOwnerProfile "$PROFILE"
  SetShellVarContext all
  Call ProfileAvailable
FunctionEnd

Function VerifyDirectory
  ; Validate only when leaving the directory page or starting a silent install.
  ; Never rewrite $INSTDIR while NSIS is validating the directory edit control.
  Push $0
  Push $1
  ClearErrors
  ; NSIS GetFullPathName can fail for a destination that does not exist yet.
  ; Win32 GetFullPathNameW normalizes a new directory without requiring it to exist.
  System::Call 'kernel32::GetFullPathNameW(w "$INSTDIR", i ${NSIS_MAX_STRLEN}, w .r1, p 0) i .r0'
  ${If} $0 == 0
  ${OrIf} $0 >= ${NSIS_MAX_STRLEN}
  ${OrIf} $1 == ""
    Pop $1
    Pop $0
    MessageBox MB_OK|MB_ICONSTOP "Укажите полный путь папки установки, например $PROGRAMFILES64\1C-Backup." /SD IDOK
    SetErrorLevel 1
    Abort
  ${EndIf}
  ${GetRoot} "$1" $0
  ${If} $1 == "$0"
  ${OrIf} $1 == "$0\"
  ${OrIf} $1 == "$InstallOwnerLocalData\OneCBackup"
  ${OrIf} $1 == "$InstallOwnerLocalData"
  ${OrIf} $1 == "$InstallOwnerProfile"
    Pop $1
    Pop $0
    MessageBox MB_OK|MB_ICONSTOP "Выберите отдельную папку для программы. Корень диска, папку пользователя и рабочий профиль резервного копирования использовать нельзя. Рекомендуется $PROGRAMFILES64\1C-Backup." /SD IDOK
    SetErrorLevel 1
    Abort
  ${EndIf}
  Pop $1
  Pop $0
  ; A corrected path must clear the prior failed validation exit code.
  SetErrorLevel 0
FunctionEnd

Function .onVerifyInstDir
  Call VerifyDirectory
FunctionEnd

Function un.onInit
  SetRegView 64
  SetShellVarContext current
  StrCpy $InstallOwnerLocalData "$LOCALAPPDATA"
  StrCpy $InstallOwnerProfile "$PROFILE"
  SetShellVarContext all
  Call un.ProfileAvailable
  ReadRegStr $0 HKLM "SYSTEM\CurrentControlSet\Services\OneCBackupAgent" "ImagePath"
  StrCpy $1 '"$INSTDIR\OneCBackup.Agent.exe" --service '
  StrLen $2 $1
  StrCpy $0 $0 $2
  ${If} $0 == $1
    MessageBox MB_OK|MB_ICONSTOP "Перед удалением программы удалите службу в настройках 1C-Backup. Задания и копии сохранятся." /SD IDOK
    Abort
  ${EndIf}
FunctionEnd

Section "Программа" MainSection
  SectionIn RO
  Call VerifyDirectory
  Call VerifyServiceLocation
  Call ProfileAvailable
  Call ProgramAvailable
  ClearErrors
  CreateDirectory "$INSTDIR"
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONSTOP "Нет доступа к папке установки. Проверьте права администратора и доступ к $PROGRAMFILES64\1C-Backup." /SD IDOK
    SetErrorLevel 1
    Abort
  ${EndIf}
  ClearErrors
  GetTempFileName $0 "$INSTDIR"
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONSTOP "Не удалось записать файл в выбранную папку. Выберите папку $PROGRAMFILES64\1C-Backup либо проверьте права доступа." /SD IDOK
    SetErrorLevel 1
    Abort
  ${EndIf}
  Delete "$0"
  SetOutPath "$INSTDIR"
  File /r "${SourceFolder}/*"
  WriteUninstaller "$INSTDIR\unins000.exe"
  CreateDirectory "$SMPROGRAMS\1C-Backup"
  CreateShortCut "$SMPROGRAMS\1C-Backup\1C-Backup.lnk" "$INSTDIR\OneCBackup.Desktop.exe"
  WriteRegStr HKLM "${UninstallKey}" "DisplayName" "1C-Backup"
  WriteRegStr HKLM "${UninstallKey}" "DisplayVersion" "${AppVersion}"
  WriteRegStr HKLM "${UninstallKey}" "Publisher" "1C-Backup"
  WriteRegStr HKLM "${UninstallKey}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UninstallKey}" "DisplayIcon" "$INSTDIR\OneCBackup.Desktop.exe"
  WriteRegStr HKLM "${UninstallKey}" "UninstallString" '$\"$INSTDIR\unins000.exe$\"'
  WriteRegStr HKLM "${UninstallKey}" "QuietUninstallString" '$\"$INSTDIR\unins000.exe$\" /S'
  WriteRegDWORD HKLM "${UninstallKey}" "NoModify" 1
  WriteRegDWORD HKLM "${UninstallKey}" "NoRepair" 1
SectionEnd

Section "Ярлык на рабочем столе" DesktopSection
  CreateShortCut "$DESKTOP\1C-Backup.lnk" "$INSTDIR\OneCBackup.Desktop.exe"
SectionEnd

Function .onInstSuccess
  ; Refresh shell icon caches after replacing the EXE at the same path.
  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
FunctionEnd

Section "Uninstall"
  Call un.ProfileAvailable
  Call un.ProgramAvailable
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
  DeleteRegKey HKLM "${UninstallKey}"
SectionEnd
