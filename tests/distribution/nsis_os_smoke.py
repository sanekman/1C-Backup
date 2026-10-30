"""Run the production OS gate against simulated Windows versions; installs nothing.

Requires an isolated x64 Windows machine and makensis. --compile-only permits
checking the fixture on Linux without claiming that its Windows checks executed.
"""
import argparse
from pathlib import Path
import subprocess
import tempfile

CASES = [
    ('server-2016', '0x0A00', 14393, 3, True),
    ('server-2019', '0x0A00', 17763, 3, True),
    ('server-2022', '0x0A00', 20348, 3, True),
    ('server-2025', '0x0A00', 26100, 3, True),
    ('domain-controller-2016', '0x0A00', 14393, 2, True),
    ('server-before-floor', '0x0A00', 14392, 3, False),
    ('server-2012-r2', '0x0603', 9600, 3, False),
    ('server-2008-r2', '0x0601', 7601, 3, False),
    ('unknown-product-server-build', '0x0A00', 14393, 0, False),
    ('workstation-1607', '0x0A00', 14393, 1, False),
    ('workstation-before-22h2', '0x0A00', 19044, 1, False),
    ('workstation-22h2', '0x0A00', 19045, 1, True),
    ('workstation-11', '0x0A00', 26100, 1, True),
]


def fixture_source(root, output):
    source = (root / 'tools/windows-installer.nsi').read_text(encoding='utf-8')
    start = source.index('Function .onInit\n') + len('Function .onInit\n')
    end = source.index('  SetShellVarContext current', start)
    gate = source[start:end]
    def quote(path): return str(path).replace('$', '$$').replace('"', '$\\"')
    return r'''Unicode true
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "x64.nsh"
Var SimBuild
Var SimProduct
Var SimVersion
!macro FixtureGetWinVer Out Kind
 !if "${Kind}" == "Product"
  StrCpy ${Out} $SimProduct
 !else if "${Kind}" == "Build"
  StrCpy ${Out} $SimBuild
 !else if "${Kind}" == "NTDDIMajMin"
  StrCpy ${Out} $SimVersion
 !else
  StrCpy ${Out} 0
 !endif
!macroend
!define __WinVer_GWV '!insertmacro FixtureGetWinVer'
!include "WinVer.nsh"
Name "SP Backup OS gate fixture"
OutFile "OUTPUT"
RequestExecutionLevel user
Function .onInit
 ${GetParameters} $R0
 ${GetOptions} $R0 "/BUILD=" $SimBuild
 ${GetOptions} $R0 "/PRODUCT=" $SimProduct
 ${GetOptions} $R0 "/VERSION=" $SimVersion
GATE
 SetErrorLevel 0
 Quit
FunctionEnd
Section "No installation"
SectionEnd
'''.replace('OUTPUT', quote(output)).replace('GATE', gate)


def check(compiler, compile_only=False):
    root = Path(__file__).resolve().parents[2]
    with tempfile.TemporaryDirectory(prefix='onec-os-fixture-') as temporary:
        directory = Path(temporary)
        exe = directory / 'os-fixture.exe'
        script = directory / 'os-fixture.nsi'
        script.write_text(fixture_source(root, exe), encoding='utf-8')
        subprocess.run([str(compiler), '-V2', str(script)], check=True)
        if compile_only:
            print('OS fixture compiled; native Windows cases not executed.')
            return
        for name, version, build, product, expected in CASES:
            result = subprocess.run([str(exe), '/S', '/BUILD=' + str(build), '/PRODUCT=' + str(product),
                                     '/VERSION=' + version], timeout=30)
            if (result.returncode == 0) != expected:
                raise AssertionError(f'{name}: unexpected exit code {result.returncode}')
            print(name + ': passed')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--compiler', type=Path, default=Path('makensis'))
    parser.add_argument('--compile-only', action='store_true')
    args = parser.parse_args()
    check(args.compiler, args.compile_only)
