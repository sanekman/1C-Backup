"""Verify actual Debian packages, PostgreSQL and HTTPS; systemctl is a recorder."""
import hashlib
import html
import http.cookiejar
import json
import os
from pathlib import Path
import pwd
import re
import ssl
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
import uuid

config = Path('/etc/onec-backup-server')


def fingerprints():
    names = ['database-password.txt', 'administrator-password.txt', 'certificate-password.txt', 'server.pfx', 'private/ca.key']
    result = {name: hashlib.sha256((config / name).read_bytes()).hexdigest() for name in names}
    stored = subprocess.check_output(['runuser', '-u', 'postgres', '--', 'psql', '-X', '-qAt', '-d', 'onecbackup',
                                      '-c', 'SELECT password_hash FROM server_users'], text=True)
    result['administratorHash'] = hashlib.sha256(stored.encode()).hexdigest()
    return result


if sys.argv[1] == 'snapshot':
    assert not json.loads((config / 'installation.json').read_text())['complete']
    Path('/tmp/initial-fingerprints.json').write_text(json.dumps(fingerprints()))
    sys.exit(0)

assert json.loads((config / 'installation.json').read_text())['complete']
assert fingerprints() == json.loads(Path('/tmp/initial-fingerprints.json').read_text())
account = pwd.getpwnam('onec-backup-server')
assert os.stat(config / 'private/ca.key').st_mode & 0o777 == 0o600
assert subprocess.run(['runuser', '-u', account.pw_name, '--', 'test', '-r', str(config / 'private/ca.key')]).returncode != 0
assert subprocess.run(['runuser', '-u', account.pw_name, '--', 'test', '-r', str(config / 'server.pfx')]).returncode == 0
assert os.stat('/var/lib/onec-backup-server/keys').st_uid == account.pw_uid
assert os.stat(config / 'administrator-password.txt').st_mode & 0o777 == 0o640
settings = json.loads((config / 'server.json').read_text())
assert settings['Server']['DisplayName'] == 'Тестовый сервер'
assert settings['Server']['PublicAddress'] == 'https://192.0.2.120:8443/'
assert 'Password=' not in settings['Server']['ConnectionString']
assert subprocess.check_output(['runuser', '-u', 'postgres', '--', 'psql', '-X', '-qAt', '-d', 'onecbackup',
                                '-c', 'SELECT max(version) FROM server_schema'], text=True).strip() == '4'
assert subprocess.check_output(['runuser', '-u', 'postgres', '--', 'psql', '-X', '-qAt',
                                '-c', 'SHOW listen_addresses'], text=True).strip() == 'localhost'

context = ssl.create_default_context(cafile=str(config / 'ca.crt'))
cookies = http.cookiejar.CookieJar()
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPSHandler(context=context), urllib.request.HTTPCookieProcessor(cookies))
base = 'https://127.0.0.1:8443/'


def request(path, body=None, headers=None, form=False):
    data = urllib.parse.urlencode(body).encode() if form else json.dumps(body).encode() if body is not None else None
    fields = dict(headers or {})
    if data is not None:
        fields['Content-Type'] = 'application/x-www-form-urlencoded' if form else 'application/json'
    with opener.open(urllib.request.Request(base + path, data=data, headers=fields), timeout=5) as response:
        return response.status, response.read().decode()


assert request('health/ready')[0] == 200
untrusted = urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPSHandler(context=ssl.create_default_context()))
try:
    untrusted.open(base + 'health/ready', timeout=5)
    raise AssertionError('Untrusted server certificate was accepted')
except urllib.error.URLError as error:
    assert isinstance(error.reason, ssl.SSLCertVerificationError)
login = request('login')[1]
anti = html.unescape(re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', login).group(1))
password = (config / 'administrator-password.txt').read_text().strip()
status, panel = request('session/login', {'name': 'admin', 'password': password, '__RequestVerificationToken': anti}, form=True)
assert status == 200 and 'Агенты' in panel
session = json.loads(request('api/v1/management/session')[1])
csrf = {'X-CSRF-TOKEN': session['csrfToken']}
invitation = json.loads(request('api/v1/management/invitations', {'name': 'Native Linux fixture', 'minutes': 5}, csrf)[1])
agent_id = str(uuid.uuid4())
enrollment = json.loads(request('api/v1/enrollment', {'invitationCode': invitation['code'], 'agentId': agent_id, 'name': 'Native Linux fixture'})[1])
bearer = {'Authorization': 'Bearer ' + enrollment['token']}
assert request(f'api/v1/agents/{agent_id}/configuration', headers=bearer)[0] == 200
subprocess.run(['systemctl', 'stop', 'onec-backup-server'], check=True)
subprocess.run(['systemctl', 'enable', '--now', 'onec-backup-server'], check=True)
sys.path.insert(0, '/setup')
import install
install.wait_ready(8443)
assert request('api/v1/management/session')[0] == 200
assert request(f'api/v1/agents/{agent_id}/configuration', headers=bearer)[0] == 200
assert fingerprints() == json.loads(Path('/tmp/initial-fingerprints.json').read_text())
unit = Path('/etc/systemd/system/onec-backup-server.service').read_text()
assert 'User=onec-backup-server' in unit and 'postgresql@17-main.service' in unit
assert 'NoNewPrivileges=true' in unit and 'ProtectSystem=' not in unit and 'PrivateTmp=' not in unit
subprocess.run(['systemd-analyze', 'verify', '/etc/systemd/system/onec-backup-server.service'], check=True)
report = {'success': True, 'version': '0.1.0-preview.5', 'checks': [
    'published archive SHA256', 'Debian13 runtime dependencies', 'real PostgreSQL17 schema4',
    'loopback-only database', 'non-root server and private CA key', 'trusted HTTPS and untrusted CA rejection',
    'administrator login and CSRF', 'enrollment and agent configuration',
    'failed first service start safely resumed', 'repeat installation retains secrets and password hash',
    'changed request refused', 'persistent session and registration after process restart',
    'LXC service unit parsed without nested mount namespaces'],
    'realSystemdTested': False, 'realProxmoxTested': False}
Path('/tmp/proxmox-installer-result.json').write_text(json.dumps(report, indent=2))
print(json.dumps(report))
