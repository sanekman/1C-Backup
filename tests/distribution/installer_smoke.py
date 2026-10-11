"""Windows-only install, update refusal, installed GUI, and safe uninstall checks."""

import ctypes
from ctypes import wintypes
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid
import zipfile
import winreg


def run(executable, *arguments, success=True, timeout=120):
    result = subprocess.run([str(executable), *map(str, arguments)], timeout=timeout,
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    if (result.returncode == 0) != success:
        raise RuntimeError(f'{Path(executable).name}: unexpected exit code {result.returncode}')


def check():
    root = Path(__file__).resolve().parents[2]
    artifacts = root / 'artifacts'
    installer = artifacts / '1C-Backup-windows-x64-setup.exe'
    installation = Path(os.environ['LOCALAPPDATA']) / 'Programs' / ('OneCBackup-test-' + uuid.uuid4().hex)
    profile = Path(os.environ['LOCALAPPDATA']) / 'OneCBackup'
    profile.mkdir(parents=True, exist_ok=True)
    marker = profile / ('installer-test-' + uuid.uuid4().hex + '.json')
    marker.write_text('{"retained":"settings-and-history"}', encoding='utf-8')
    marker_bytes = marker.read_bytes()
    ui = artifacts / 'installed-ui'
    run_path = r'Software\Microsoft\Windows\CurrentVersion\Run'
    startup_name = 'OneCBackupAgent'
    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, run_path) as key:
        try:
            previous = winreg.QueryValueEx(key, startup_name)
        except FileNotFoundError:
            previous = None
    if previous is not None:
        raise RuntimeError('Installer smoke requires an isolated Windows user without agent autostart')
    installed = False
    try:
        run(installer, '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/DIR=' + str(installation))
        installed = True
        with zipfile.ZipFile(artifacts / '1C-Backup-windows-x64.zip') as archive:
            for name in archive.namelist():
                if name.endswith('/'):
                    continue
                file = installation.joinpath(*name.split('/')[1:])
                assert file.is_file(), 'Missing installed component: ' + name
                assert hashlib.sha256(file.read_bytes()).digest() == hashlib.sha256(archive.read(name)).digest(), name
        version = (installation / 'VERSION.txt').read_text(encoding='utf-8').strip()
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, run_path) as key:
            try:
                winreg.QueryValueEx(key, startup_name)
            except FileNotFoundError:
                pass
            else:
                raise AssertionError('Installation unexpectedly enabled autostart')
        shortcut = Path(os.environ['APPDATA']) / 'Microsoft/Windows/Start Menu/Programs/1C-Backup/1C-Backup.lnk'
        assert shortcut.is_file(), 'Missing Start Menu shortcut'
        # A live engine owns this exact share-denying lock in normal application use.
        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
                                      wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
        kernel.CreateFileW.restype = wintypes.HANDLE
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        lock = profile / 'engine.lock'
        handle = kernel.CreateFileW(str(lock), 0x80000000 | 0x40000000, 0, None, 4, 0x80, None)
        if handle == wintypes.HANDLE(-1).value:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            run(installer, '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/DIR=' + str(installation), success=False)
            run(installation / 'unins000.exe', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', success=False)
            assert (installation / 'OneCBackup.Desktop.exe').is_file()
            assert marker.read_bytes() == marker_bytes
        finally:
            kernel.CloseHandle(handle)
        run(installer, '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/DIR=' + str(installation))
        assert marker.read_bytes() == marker_bytes, 'Update changed user profile'
        run(installation / 'OneCBackup.Desktop.exe', '--smoke-ui', ui, timeout=180)
        result = json.loads((ui / 'smoke-result.json').read_text(encoding='utf-8-sig'))
        assert result['success'], 'Installed GUI smoke failed'
        info = json.loads((ui / 'build-info.json').read_text(encoding='utf-8-sig'))
        assert info['version'] == version
        assert info['revision'] == os.environ['GITHUB_SHA']
        # Uninstall must leave an autostart pointing at a different portable copy alone.
        foreign = '"C:\\Portable\\OneCBackup.Agent.exe" --run --quiet'
        with winreg.CreateKey(winreg.HKEY_CURRENT_USER, run_path) as key:
            winreg.SetValueEx(key, startup_name, 0, winreg.REG_SZ, foreign)
        run(installation / 'unins000.exe', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
        installed = False
        assert marker.read_bytes() == marker_bytes
        assert not (installation / 'OneCBackup.Desktop.exe').exists()
        assert not shortcut.exists()
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, run_path) as key:
            assert winreg.QueryValueEx(key, startup_name)[0] == foreign
            winreg.DeleteValue(key, startup_name)
        # A second install checks removal of this installation's own autostart.
        run(installer, '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/DIR=' + str(installation))
        installed = True
        with winreg.CreateKey(winreg.HKEY_CURRENT_USER, run_path) as key:
            winreg.SetValueEx(key, startup_name, 0, winreg.REG_SZ,
                             '"' + str(installation / 'OneCBackup.Agent.exe') + '" --run --quiet')
        run(installation / 'unins000.exe', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
        installed = False
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, run_path) as key:
            try:
                winreg.QueryValueEx(key, startup_name)
            except FileNotFoundError:
                pass
            else:
                raise AssertionError('Uninstall left its own autostart')
        assert marker.read_bytes() == marker_bytes
        summary = {'success': True, 'version': version, 'revision': info['revision'],
                   'checks': ['installed-bytes', 'no-auto-start', 'start-menu', 'active-engine-refusal', 'profile-preserved-on-update',
                              'installed-gui', 'profile-preserved', 'foreign-startup-preserved', 'owned-startup-removed']}
        (artifacts / 'installer-result.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
        print(json.dumps(summary))
    finally:
        if installed and (installation / 'unins000.exe').exists():
            run(installation / 'unins000.exe', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
        with winreg.CreateKey(winreg.HKEY_CURRENT_USER, run_path) as key:
            try:
                winreg.DeleteValue(key, startup_name)
            except FileNotFoundError:
                pass
        marker.unlink(missing_ok=True)


if __name__ == '__main__':
    if sys.platform != 'win32':
        raise SystemExit('Installer smoke requires Windows')
    check()
