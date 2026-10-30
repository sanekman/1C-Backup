"""Install the published alpha.25, create an encrypted backup, then upgrade in place."""
import json
import os
import shutil
from pathlib import Path
import subprocess
import sys
import winreg

from legacy_installer import VERSION, SHA256, download

UNINSTALL_KEY = r'Software\Microsoft\Windows\CurrentVersion\Uninstall\{142FA512-738F-4BE8-9C31-487B3500B30B}_is1'
RUN_KEY = r'Software\Microsoft\Windows\CurrentVersion\Run'
RUN_VALUE = 'OneCBackupAgent'


def run(*command, timeout=180):
    result = subprocess.run(list(map(str, command)), timeout=timeout)
    if result.returncode:
        raise RuntimeError(Path(str(command[0])).name + ': nonzero exit code ' + str(result.returncode))


def registration():
    with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, UNINSTALL_KEY) as key:
        return {name: winreg.QueryValueEx(key, name)[0] for name in ('InstallLocation', 'DisplayName', 'UninstallString')}


def startup():
    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, RUN_KEY) as key:
        try:
            return winreg.QueryValueEx(key, RUN_VALUE)[0]
        except FileNotFoundError:
            return None


def main():
    if sys.platform != 'win32' or os.getenv('GITHUB_ACTIONS') != 'true' or os.getenv('SPBACKUP_UPGRADE_SMOKE') != '1':
        raise RuntimeError('Requires an explicitly enabled isolated Windows CI runner')
    root = Path(__file__).resolve().parents[2]
    artifacts = root / 'artifacts'
    state = artifacts / 'windows-upgrade-private'
    if state.exists():
        raise FileExistsError('Refusing to replace an existing upgrade fixture')
    # Never install over a pre-existing application or run jobs from another profile.
    try:
        registration()
    except FileNotFoundError:
        pass
    else:
        raise RuntimeError('Upgrade test requires an isolated runner without an installed application')
    profile = Path(os.environ['LOCALAPPDATA']) / 'OneCBackup'
    profile_entries = {path.name for path in profile.iterdir()} if profile.exists() else set()
    if profile_entries - {'engine.lock'} or startup() is not None:
        raise RuntimeError('Upgrade test requires an unused agent profile and autostart')
    installation = Path(os.environ['ProgramFiles']) / '1C-Backup'
    if installation.exists() or (Path(os.environ['ProgramFiles']) / 'SP Backup').exists():
        raise RuntimeError('Installation directories already exist; no files changed')
    state.mkdir()
    version = (artifacts / 'distribution-version.txt').read_text().strip()
    current = artifacts / ('SP-Backup-' + version + '-windows-x64-setup.exe')
    previous = download(state, token=os.getenv('GH_TOKEN'))
    project = root / 'tests/WindowsUpgradeProbe/WindowsUpgradeProbe.csproj'
    old_probe = state / 'old-probe/WindowsUpgradeProbe.dll'
    new_probe = state / 'new-probe/WindowsUpgradeProbe.dll'
    checks = []
    installed = False
    stage = 'install-alpha25'
    try:
        run(previous, '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/DIR=' + str(installation))
        installed = True
        assert Path(registration()['InstallLocation']) == installation
        assert registration()['DisplayName'].startswith('1C-Backup'), 'Unexpected legacy installed-program name'
        assert (installation / 'VERSION.txt').read_text().strip() == VERSION
        checks.append('published-alpha25-pinned-checksum-and-installation')
        stage = 'legacy-profile-and-backup'
        run('dotnet', 'build', project, '-c', 'Release', '-o', old_probe.parent,
            '-p:LegacyDistribution=' + str(installation), '-m:1', '-nr:false', '-p:UseSharedCompilation=false')
        # Preserve the published companion assemblies before the installer replaces them.
        for dependency in installation.glob('*.dll'):
            if dependency.name.startswith(('OneCBackup.', 'ICSharpCode.', 'FluentFTP')):
                shutil.copy2(dependency, old_probe.parent / dependency.name)
        run('dotnet', old_probe, 'prepare', state, installation)
        fixture = json.loads((state / 'fixture.json').read_text())
        job_id = fixture['JobId']
        run(installation / 'OneCBackup.Agent.exe', '--run-job', job_id, '--data-dir', profile)
        run('dotnet', old_probe, 'snapshot', state, installation)
        checks.append('old-binary-encrypted-backup-and-profile')
        legacy_tray = '"' + str(installation / 'OneCBackup.Desktop.exe') + '" --tray --start-agent'
        with winreg.CreateKey(winreg.HKEY_CURRENT_USER, RUN_KEY) as key:
            winreg.SetValueEx(key, RUN_VALUE, 0, winreg.REG_SZ, legacy_tray)
        stage = 'upgrade-without-directory-override'
        # No /DIR: the same installer ID must retain the old installation directory.
        run(current, '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-')
        assert Path(registration()['InstallLocation']) == installation
        assert registration()['DisplayName'].startswith('SP Backup'), 'Unexpected new installed-program name'
        assert (installation / 'VERSION.txt').read_text().strip() == version
        assert not (Path(os.environ['ProgramFiles']) / 'SP Backup').exists()
        assert startup() == legacy_tray, 'Installer changed an existing autostart'
        checks.append('in-place-upgrade-retains-directory-and-installer-id')
        shortcuts = Path(os.environ['PROGRAMDATA']) / 'Microsoft/Windows/Start Menu/Programs'
        assert (shortcuts / 'SP Backup/SP Backup.lnk').is_file()
        assert not (shortcuts / '1C-Backup/1C-Backup.lnk').exists()
        for component in ('Agent', 'Desktop'):
            assert (installation / ('SPBackup.' + component + '.exe')).read_bytes() == (installation / ('OneCBackup.' + component + '.exe')).read_bytes()
        checks.append('new-shortcuts-and-compatible-apphosts')
        stage = 'read-old-secrets-and-restore'
        run('dotnet', 'build', project, '-c', 'Release', '-o', new_probe.parent, '-m:1', '-nr:false', '-p:UseSharedCompilation=false')
        run('dotnet', new_probe, 'tls-write', state, installation)
        run('dotnet', old_probe, 'tls-old-read', state, installation)
        checks.append('legacy-agent-skips-protected-job-and-restores-ordinary-copy')
        run('dotnet', new_probe, 'verify-one', state, installation)
        checks.append('unchanged-profile-vault-dpapi-export-and-old-copy-restoration')
        stage = 'legacy-desktop-launch-and-autostart-migration'
        ui = state / 'ui'
        run(installation / 'OneCBackup.Desktop.exe', '--smoke-ui', ui)
        result = json.loads((ui / 'smoke-result.json').read_text(encoding='utf-8-sig'))
        info = json.loads((ui / 'build-info.json').read_text(encoding='utf-8-sig'))
        assert result['success'] and info['revision'] == os.environ['GITHUB_SHA']
        expected_tray = '"' + str(installation / 'SPBackup.Desktop.exe') + '" --tray --start-agent'
        assert startup() == expected_tray, 'Owned legacy autostart was not migrated'
        checks.append('old-desktop-name-runs-new-gui-and-migrates-autostart')
        stage = 'new-backup-through-legacy-agent-path'
        run(installation / 'OneCBackup.Agent.exe', '--run-job', job_id, '--data-dir', profile)
        run('dotnet', new_probe, 'verify-two', state, installation)
        checks.append('new-backup-preserves-and-restores-old-and-new-copies')
        stage = 'uninstall'
        uninstaller = Path(registration()['UninstallString'].strip('"'))
        assert uninstaller.parent == installation
        run(uninstaller, '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
        installed = False
        assert startup() is None
        assert not (installation / 'SPBackup.Desktop.exe').exists()
        run('dotnet', new_probe, 'verify-after-uninstall', state, installation)
        checks.append('uninstall-preserves-profile-secrets-and-both-backups')
        summary = {'success': True, 'source': os.environ['GITHUB_SHA'], 'previousVersion': VERSION,
                   'previousInstallerSha256': SHA256, 'currentVersion': version, 'checks': checks}
        (artifacts / 'windows-upgrade-result.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
        print(json.dumps(summary))
    except Exception as error:
        (artifacts / 'windows-upgrade-failure.json').write_text(json.dumps({'stage': stage, 'errorType': type(error).__name__}), encoding='utf-8')
        raise
    finally:
        cleanup_errors = []
        if (state / 'fixture.json').exists():
            probe = new_probe if new_probe.exists() else old_probe
            if probe.exists():
                try:
                    run('dotnet', probe, 'cleanup', state, installation)
                except Exception as error:
                    cleanup_errors.append(error)
        if installed:
            try:
                run(Path(registration()['UninstallString'].strip('"')), '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
            except Exception as error:
                cleanup_errors.append(error)
        with winreg.CreateKey(winreg.HKEY_CURRENT_USER, RUN_KEY) as key:
            value = startup()
            owned = ['"' + str(installation / name) + '" --tray --start-agent'
                     for name in ('OneCBackup.Desktop.exe', 'SPBackup.Desktop.exe')]
            if value in owned:
                winreg.DeleteValue(key, RUN_VALUE)
        if cleanup_errors:
            raise RuntimeError('Upgrade fixture cleanup failed') from cleanup_errors[0]


if __name__ == '__main__':
    main()
