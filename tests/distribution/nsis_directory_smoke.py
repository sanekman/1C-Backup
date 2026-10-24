"""Exercise the NSIS directory-page button with the production validation function.

Run on an isolated Windows account with NSIS installed. The fixture never installs
application files, registers shortcuts or touches the backup profile.
"""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile


def fixture_source(root, output, result):
    source = (root / 'tools/windows-installer.nsi').read_text(encoding='utf-8')
    start = source.index('Function VerifyDirectory\n')
    end = source.index('\nFunction un.onInit', start)
    validation = source[start:end]
    policy = '!define MUI_DIRECTORYPAGE_VERIFYONLEAVE' if '!define MUI_DIRECTORYPAGE_VERIFYONLEAVE' in source else ''
    def quote(path): return str(path).replace('$', '$$').replace('"', '$\\"')
    return r'''Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
Name "1C-Backup directory verification fixture"
OutFile "OUTPUT"
InstallDir "$PROGRAMFILES64\1C-Backup-test"
RequestExecutionLevel user
Var Phase
Var InstallOwnerLocalData
Var InstallOwnerProfile
POLICY
!define MUI_PAGE_CUSTOMFUNCTION_SHOW FixtureShow
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Russian"
Function .onInit
 SetShellVarContext current
 StrCpy $InstallOwnerLocalData "$LOCALAPPDATA"
 StrCpy $InstallOwnerProfile "$PROFILE"
 StrCpy $Phase 0
 IfSilent 0 +2
 SetErrorLevel 1
FunctionEnd
Function FixtureShow
 GetFunctionAddress $0 FixtureCheck
 nsDialogs::CreateTimer $0 300
FunctionEnd
Function FixtureCheck
 GetDlgItem $0 $HWNDPARENT 1
 System::Call 'user32::IsWindowEnabled(p r0) i .r1'
 FileOpen $2 "RESULT" a
 FileWrite $2 "$Phase|$1$\r$\n"
 FileClose $2
 IntOp $Phase $Phase + 1
 ${If} $Phase == 1
  SendMessage $mui.DirectoryPage.Directory ${WM_SETTEXT} 0 "STR:$LOCALAPPDATA\Backup Apps\1C-Backup"
 ${ElseIf} $Phase == 2
  SendMessage $mui.DirectoryPage.Directory ${WM_SETTEXT} 0 "STR:$LOCALAPPDATA\Программы\1C-Backup"
 ${Else}
  Quit
 ${EndIf}
FunctionEnd
VALIDATION
Section "Fixture"
 Call VerifyDirectory
SectionEnd
'''.replace('OUTPUT', quote(output)).replace('RESULT', quote(result)).replace('POLICY', policy).replace('VALIDATION', validation)


def check(compiler):
    if os.name != 'nt':
        raise RuntimeError('Run this GUI smoke test on Windows; Wine results require a separate explicit harness')
    root = Path(__file__).resolve().parents[2]
    with tempfile.TemporaryDirectory(prefix='onec-directory-page-') as temporary:
        base = Path(temporary); exe = base / 'fixture.exe'; result = base / 'result.txt'; script = base / 'fixture.nsi'
        script.write_text(fixture_source(root, exe, result), encoding='utf-8')
        subprocess.run([compiler, '-V2', str(script)], check=True)
        subprocess.run([str(exe)], check=True, timeout=30)
        lines = result.read_text().splitlines()
        assert len(lines) == 3, lines
        assert all(line.split('|')[1] == '1' for line in lines), 'Install button disabled for valid path: ' + repr(lines)
        # A never-created destination must validate and clear a prior failure exit code.
        fresh = base / 'new parent' / 'new destination'
        assert not fresh.exists()
        accepted = subprocess.run([str(exe), '/S', '/D=' + str(fresh)], timeout=15)
        assert accepted.returncode == 0, 'New directory rejected: ' + str(fresh)
        assert not fresh.exists(), 'Fixture must not create an installation directory'
        # Silent installs also validate roots/profile directories, before any writes.
        for folder in [Path(os.environ['LOCALAPPDATA']).anchor, os.environ['LOCALAPPDATA'],
                       str(Path(os.environ['LOCALAPPDATA']) / 'OneCBackup'), os.environ['USERPROFILE']]:
            rejected = subprocess.run([str(exe), '/S', '/D=' + str(folder)], timeout=15)
            assert rejected.returncode != 0, 'Unsafe directory accepted: ' + str(folder)
        print('Directory page: default, spaces and Cyrillic paths enable Install; protected roots rejected.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--compiler', default='makensis.exe')
    check(parser.parse_args().compiler)
