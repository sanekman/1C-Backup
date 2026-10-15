"""Check a released Linux upgrade in disposable Debian 12; systemctl is a recorder."""
import argparse
import json
from pathlib import Path
import subprocess
import tempfile
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--previous-archive', type=Path, required=True)
    parser.add_argument('--previous-installer', type=Path, required=True)
    parser.add_argument('--archive', type=Path, required=True)
    parser.add_argument('--from-github', action='store_true')
    args = parser.parse_args()
    repository = Path(__file__).resolve().parents[2]
    for path in [args.previous_archive, args.previous_installer, args.archive]:
        if not path.is_file():
            parser.error('Input file does not exist: ' + str(path))
    image = 'onec-storage-upgrade-' + uuid.uuid4().hex[:12]
    container = image + '-check'

    def run(command, **kwargs):
        result = subprocess.run(command, capture_output=True, text=True, **kwargs)
        if result.returncode:
            raise RuntimeError('Upgrade check failed: ' + result.stderr[-2000:])
        return result

    with tempfile.TemporaryDirectory(prefix='onec-storage-upgrade-') as directory:
        dockerfile = Path(directory) / 'Dockerfile'
        dockerfile.write_text('FROM debian:bookworm-slim\nRUN apt-get update && apt-get install -y git ca-certificates libicu72 libssl3 zlib1g libstdc++6 python3\n')
        run(['docker', 'build', '-t', image, directory])
        try:
            run(['docker', 'run', '-d', '--name', container,
                 '-v', str(args.previous_archive.resolve().parent) + ':/previous:ro',
                 '-v', str(args.archive.resolve().parent) + ':/current:ro',
                 '-v', str(args.previous_installer.resolve()) + ':/old-install.sh:ro',
                 '-v', str(repository / 'deploy/storage') + ':/setup:ro',
                 '-v', '/etc/ssl/certs/ca-certificates.crt:/run/network-ca.crt:ro',
                 '-e', 'GIT_SSL_CAINFO=/run/network-ca.crt', image, 'sleep', 'infinity'])
            setup = """set -eu
mkdir -p /usr/local/bin
cat >/usr/local/bin/systemctl <<'SH'
#!/bin/sh
printf '%s\\n' "$*" >>/tmp/systemctl-calls.txt
SH
chmod +x /usr/local/bin/systemctl
bash /old-install.sh --archive "/previous/$1" --storage /backups --endpoint wss://relay.example.com
python3 - <<'PY'
import hashlib,json
from pathlib import Path
root=Path('/etc/onec-backup-storage')
def fingerprint(path):
 data=path.read_bytes()
 if path.suffix=='.json':data=json.dumps(json.loads(data),sort_keys=True).encode()
 return hashlib.sha256(data).hexdigest()
snapshot={path.name:fingerprint(path) for path in root.iterdir() if path.is_file()}
pairings=list(Path('/root').glob('onec-backup-storage-pairing.*/client-pairing.json'))
assert len(pairings)==1
snapshot['pairing']=hashlib.sha256(pairings[0].read_bytes()).hexdigest()
snapshot['old_version']=Path('/opt/onec-backup-storage/current/VERSION.txt').read_text().strip()
Path('/backups/upgrade-sentinel.bin').write_bytes(b'previous verified backup\\0unchanged')
Path('/tmp/before.json').write_text(json.dumps(snapshot))
PY
"""
            run(['docker', 'exec', container, 'bash', '-c', setup, 'setup', args.previous_archive.name])
            current = '/current/' + args.archive.name

            def install(archive, github=False):
                command = ['bash', '/setup/install-from-github.sh'] if github else [
                    'bash', '/setup/install-lxc.sh', '--archive', archive]
                run(['docker', 'exec', container, *command, '--storage', '/backups'])

            install(current, args.from_github)
            verify = """import hashlib,json,os,sys
from pathlib import Path
before=json.loads(Path('/tmp/before.json').read_text())
root=Path('/etc/onec-backup-storage')
for name,expected in before.items():
 if name in ['pairing','old_version']:continue
 path=root/name
 data=path.read_bytes()
 if path.suffix=='.json':data=json.dumps(json.loads(data),sort_keys=True).encode()
 assert hashlib.sha256(data).hexdigest()==expected, 'Configuration changed: '+name
pairings=list(Path('/root').glob('onec-backup-storage-pairing.*/client-pairing.json'))
assert len(pairings)==1 and hashlib.sha256(pairings[0].read_bytes()).hexdigest()==before['pairing']
assert Path('/backups/upgrade-sentinel.bin').read_bytes()==b'previous verified backup\\0unchanged'
assert Path('/opt/onec-backup-storage/'+before['old_version']).is_dir()
assert os.stat(root).st_mode & 0o777 == 0o700
assert all(os.stat(path).st_mode & 0o777 == 0o600 for path in root.iterdir() if path.is_file())
assert Path('/opt/onec-backup-storage/current/VERSION.txt').read_text().strip()==sys.argv[1]
"""
            version = (repository / 'deploy/storage/VERSION').read_text().strip()

            def check(expected):
                run(['docker', 'exec', container, 'python3', '-c', verify, expected])

            check(version)
            install(current)
            check(version)
            conflict = """import hashlib,subprocess,sys
from pathlib import Path
payload=bytearray(Path(sys.argv[1]).read_bytes())
assert payload[:2]==b'\\x1f\\x8b' and not payload[3]&2
payload[4]^=1
archive=Path('/tmp/conflicting-package.tar.gz');archive.write_bytes(payload)
archive.with_suffix('.gz.sha256').write_text(hashlib.sha256(payload).hexdigest()+'  '+archive.name+'\\n')
calls=Path('/tmp/systemctl-calls.txt').read_bytes()
result=subprocess.run(['bash','/setup/install-lxc.sh','--archive',str(archive),'--storage','/backups'],capture_output=True,text=True)
assert result.returncode!=0 and 'Existing version differs' in result.stderr+result.stdout
assert Path('/tmp/systemctl-calls.txt').read_bytes()==calls
"""
            run(['docker', 'exec', container, 'python3', '-c', conflict, current])
            check(version)
            old_version = run(['docker', 'exec', container, 'python3', '-c',
                               "import json;print(json.load(open('/tmp/before.json'))['old_version'])"]).stdout.strip()
            install('/previous/' + args.previous_archive.name)
            check(old_version)
            install(current)
            check(version)
            print(json.dumps({'Success': True, 'PreviousVersion': old_version, 'Version': version,
                              'InstallFromGitHub': args.from_github, 'DataAndPairingPreserved': True,
                              'ConfigurationPermissionsPreserved': True, 'RepeatInstallVerified': True,
                              'ConflictingSameVersionRejectedBeforeRestart': True,
                              'RollbackAndUpgradeVerified': True, 'NativeSystemdOrProxmoxTested': False}))
        finally:
            subprocess.run(['docker', 'rm', '-f', container], capture_output=True)
            subprocess.run(['docker', 'image', 'rm', image], capture_output=True)


if __name__ == '__main__':
    main()
