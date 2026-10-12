"""Exercise the actual PowerShell handoff with mocked registry and process launches; never installs an EXE."""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import tempfile

IMAGE = 'mcr.microsoft.com/powershell@sha256:206a748b34deec1b64553fcfa92294fc871c2df5855e9f340df172756bef201f'


def run(use_docker=False):
    source = Path(__file__).resolve().parents[2] / 'src/OneCBackup.Desktop/UpdateHandoff.ps1'
    fixture = Path(__file__).with_name('update_handoff_fixture.ps1')
    shell = shutil.which('powershell') or shutil.which('pwsh')
    if not use_docker and shell is None:
        raise RuntimeError('PowerShell is required; or use --docker with the pinned Microsoft image already pulled')
    results = []
    with tempfile.TemporaryDirectory(prefix='onec-update-handoff-') as temporary:
        root = Path(temporary)
        shutil.copyfile(fixture, root / fixture.name)
        for case in ['success', 'inno', 'restart', 'corrupt', 'wrong-directory', 'wrong-version', 'installer-failure', 'parent-timeout', 'machine-registration', 'legacy-registration']:
            if use_docker:
                command = ['docker', 'run', '--init', '--rm', '--user', '1000:1000', '--network', 'none',
                           '--env', 'POWERSHELL_TELEMETRY_OPTOUT=1',
                           '--mount', 'type=bind,source=' + str(root) + ',target=/work',
                           '--mount', 'type=bind,source=' + str(source) + ',target=/source/UpdateHandoff.ps1,readonly',
                           IMAGE, 'pwsh', '-NoLogo', '-NoProfile', '-NonInteractive', '-File',
                           '/work/' + fixture.name, case, '/work', '/source/UpdateHandoff.ps1']
                fixture_base = '/work'
            else:
                command = [shell, '-NoLogo', '-NoProfile', '-NonInteractive', '-File', str(fixture), case, str(root), str(source)]
                fixture_base = str(root)
            completed = subprocess.run(command, capture_output=True, text=True, timeout=40)
            result_path = root / case / 'updates/update-result.json'
            if not result_path.exists():
                raise RuntimeError((case, completed.returncode, completed.stdout, completed.stderr))
            result = json.loads(result_path.read_text(encoding='utf-8-sig'))
            expected = case in ['success', 'inno', 'restart', 'machine-registration', 'legacy-registration']
            assert result['Success'] == expected, (case, result, completed.stderr)
            assert completed.returncode == (0 if expected else 1), (case, completed.stdout, completed.stderr)
            calls_file = root / case / 'calls.jsonl'
            calls = [json.loads(line) for line in calls_file.read_text(encoding='utf-8-sig').splitlines()] if calls_file.exists() else []
            assert len(calls) == (2 if case == 'restart' else 1 if case in ['success', 'inno', 'wrong-version', 'installer-failure', 'machine-registration', 'legacy-registration'] else 0), (case, calls)
            location = str(Path(fixture_base) / case / 'application with spaces') if not use_docker else fixture_base + '/' + case + '/application with spaces'
            if case == 'success': assert calls[0]['Arguments'] == ['/S', '/D=' + location], calls
            if case == 'inno': assert calls[0]['Arguments'] == ['/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/DIR="' + location + '"'], calls
            if case == 'restart': assert calls[1]['Arguments'] == ['--start-agent'], calls
            results.append({'case': case, 'passed': True})
    return {'success': True, 'cases': results, 'registry_and_installer_mocked': True, 'native_windows_installation_tested': False}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--docker', action='store_true')
    args = parser.parse_args()
    print(json.dumps(run(args.docker)))
