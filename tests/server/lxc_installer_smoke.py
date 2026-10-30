"""Run the native server installer in disposable Debian 13, with a systemctl recorder."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import uuid

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('installer', ROOT / 'deploy/server/create-proxmox-lxc.py')
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)


def docker(*args, **kwargs):
    environment = dict(os.environ)
    for name in ['DOCKER_HOST', 'DOCKER_CONTEXT', 'DOCKER_TLS', 'DOCKER_TLS_VERIFY', 'DOCKER_CERT_PATH']:
        environment.pop(name, None)
    return subprocess.run(['docker', '--host=unix:///var/run/docker.sock', *map(str, args)], env=environment, **kwargs)


def main():
    identity = 'sp-server-installer-' + uuid.uuid4().hex[:12]
    ca = Path(os.environ.get('SSL_CERT_FILE') or '/etc/ssl/certs/ca-certificates.crt')
    if not ca.is_file():
        raise RuntimeError('A system CA bundle is required for the fixture network.')
    docker('info', check=True, stdout=subprocess.DEVNULL)
    with tempfile.TemporaryDirectory(prefix='sp-server-install-test-') as work:
        work = Path(work)
        packages = work / 'packages'; packages.mkdir()
        installer.download(f'https://github.com/{installer.REPOSITORY}/releases/download/server-v{installer.VERSION}/server-release.json', packages / 'server-release.json', 1024 * 1024)
        asset = installer.package_asset(json.loads((packages / 'server-release.json').read_text()), installer.VERSION)
        installer.download(asset['url'], packages / installer.ARCHIVE, asset['bytes'])
        (work / 'Dockerfile').write_text('''FROM debian:trixie-slim
RUN --mount=type=secret,id=build-ca,required=true \\
    apt-get -o Acquire::https::CaInfo=/run/secrets/build-ca update && \\
    apt-get -o Acquire::https::CaInfo=/run/secrets/build-ca install -y --no-install-recommends python3 ca-certificates systemd procps iproute2
''')
        docker('build', '--secret', 'id=build-ca,src=' + str(ca), '-t', identity, work, check=True)
        try:
            docker('run', '-d', '--name', identity,
                   '--mount', f'type=bind,src={packages},dst=/packages,readonly',
                   '--mount', f'type=bind,src={ROOT / "deploy/server/create-proxmox-lxc.py"},dst=/setup/install.py,readonly',
                   '--mount', f'type=bind,src={ROOT / "tests/server/proxmox_systemctl.py"},dst=/setup/systemctl.py,readonly',
                   '--mount', f'type=bind,src={ROOT / "tests/server/proxmox_guest_probe.py"},dst=/setup/probe.py,readonly',
                   '--mount', f'type=bind,src={ca},dst=/run/proxy-ca.pem,readonly',
                   '-e', 'SSL_CERT_FILE=/run/proxy-ca.pem', identity, 'sleep', 'infinity', check=True, stdout=subprocess.DEVNULL)
            setup = '''set -eu
printf 'Acquire::https::CaInfo "/run/proxy-ca.pem";\n' >/etc/apt/apt.conf.d/99fixture-ca
cp /setup/systemctl.py /usr/local/bin/systemctl
chmod 755 /usr/local/bin/systemctl
touch /tmp/reject-first-server-start
cat >/tmp/request.json <<'JSON'
{"version":"0.1.0-preview.5","address":"https://192.0.2.120:8443/","name":"Тестовый сервер","port":8443}
JSON
if python3 /setup/install.py --inside-container --request /tmp/request.json --package-directory /packages; then
 echo 'First service start failure was ignored' >&2; exit 1
fi
python3 /setup/probe.py snapshot
python3 /setup/install.py --inside-container --request /tmp/request.json --package-directory /packages
python3 /setup/install.py --inside-container --request /tmp/request.json --package-directory /packages
sed 's/Тестовый сервер/Другой сервер/' /tmp/request.json >/tmp/changed-request.json
if python3 /setup/install.py --inside-container --request /tmp/changed-request.json --package-directory /packages; then
 echo 'Changed installation request was accepted' >&2; exit 1
fi
python3 /setup/probe.py verify
'''
            docker('exec', identity, 'bash', '-c', setup, check=True)
            report = ROOT / 'artifacts/server-tests/proxmox-installer-result.json'
            report.parent.mkdir(parents=True, exist_ok=True)
            docker('cp', identity + ':/tmp/proxmox-installer-result.json', report, check=True)
            result = json.loads(report.read_text())
            assert result['success'] and not result['realSystemdTested'] and not result['realProxmoxTested']
            print('PASS: native server installation on Debian 13, actual PG/HTTPS; systemctl recorded, no real Proxmox.')
        finally:
            docker('rm', '-f', identity, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            docker('image', 'rm', identity, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


if __name__ == '__main__':
    main()
