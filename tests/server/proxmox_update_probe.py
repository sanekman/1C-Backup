"""Actual Debian/PG/package update; controlled failures, systemctl recorder, no real Proxmox."""
import hashlib
import html
import http.cookiejar
import importlib.util
import json
from pathlib import Path
import re
import ssl
import subprocess
import urllib.parse
import urllib.request
import uuid
from unittest.mock import patch

spec=importlib.util.spec_from_file_location('native','/setup/install.py')
m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
checks=[]
packages=Path('/packages')
backup=Path('/var/backups/onec-backup-server')
config=m.CONFIG

def cli(version, directory=None):
    return subprocess.run(['python3','/setup/install.py','--inside-container','--update','--version',version,
                           '--package-directory',str(directory or packages/version)],capture_output=True)

def pg(statement):
    return subprocess.check_output(['runuser','-u','postgres','--','psql','-X','-qAt','-d','onecbackup','-c',statement],text=True).strip()

def secrets_hash():
    result={name:hashlib.sha256((config/name).read_bytes()).hexdigest() for name in
            ['database-password.txt','administrator-password.txt','certificate-password.txt','server.pfx','private/ca.key']}
    result['admin']=pg('SELECT password_hash FROM server_users')
    return result

original=secrets_hash()
assert cli('0.1.0-preview.8').returncode==0
assert pg('SELECT max(version) FROM server_schema')=='5'
assert (m.APPLICATION/'current').resolve().name=='0.1.0-preview.8'
assert secrets_hash()==original
checks.append('published-preview5-to-preview8-preparation-preserves-credentials-and-data')
context=ssl.create_default_context(cafile=str(config/'ca.crt'))
opener=urllib.request.build_opener(urllib.request.ProxyHandler({}),urllib.request.HTTPSHandler(context=context),urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
base='https://127.0.0.1:8443/'
def request(path, body=None, headers=None, form=False):
    data=urllib.parse.urlencode(body).encode() if form else json.dumps(body).encode() if body is not None else None
    fields=dict(headers or {})
    if data is not None:fields['Content-Type']='application/x-www-form-urlencoded' if form else 'application/json'
    with opener.open(urllib.request.Request(base+path,data=data,headers=fields),timeout=5) as response:return response.status,response.read().decode()
page=request('login')[1]
anti=html.unescape(re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"',page).group(1))
assert request('session/login',{'name':'admin','password':(config/'administrator-password.txt').read_text().strip(),'__RequestVerificationToken':anti},form=True)[0]==200
session=json.loads(request('api/v1/management/session')[1]);headers={'X-CSRF-TOKEN':session['csrfToken']}
invite=json.loads(request('api/v1/management/invitations',{'name':'Update fixture','minutes':5},headers)[1])
agent=str(uuid.uuid4());enrolled=json.loads(request('api/v1/enrollment',{'invitationCode':invite['code'],'agentId':agent,'name':'Update fixture'})[1])
bearer={'Authorization':'Bearer '+enrolled['token']}
def verify_previous():
    assert (m.APPLICATION/'current').resolve().name=='0.1.0-preview.8'
    assert pg('SELECT max(version) FROM server_schema')=='5'
    assert pg("SELECT to_regclass('update_failure_marker')") == ''
    assert secrets_hash()==original
    assert not (m.DATA/'update_failure_marker').exists()
    assert not (m.APPLICATION/'0.1.0-preview.8/update_failure_marker').exists()
    assert request('api/v1/management/session')[0]==200
    assert request(f'api/v1/agents/{agent}/configuration',headers=bearer)[0]==200

def damage():
    pg('CREATE TABLE update_failure_marker(id int)')
    (config/'database-password.txt').write_text('changed fixture secret')
    (m.DATA/'update_failure_marker').write_text('changed')
    (m.APPLICATION/'0.1.0-preview.8/update_failure_marker').write_text('changed')

for failure in ['migration','readiness']:
    real_run=m.run;real_ready=m.wait_ready;calls=0
    def run(args,**kwargs):
        result=real_run(args,**kwargs)
        if failure=='migration' and str(args[-1])=='--migrate':
            assert pg('SELECT max(version) FROM server_schema')=='6'
            damage()
            raise RuntimeError('controlled committed migration failure')
        return result
    def ready(port):
        global calls
        calls+=1
        result=real_ready(port)
        if failure=='readiness' and calls==2:
            damage()
            raise RuntimeError('controlled readiness failure')
        return result
    try:
        with patch.object(m,'run',side_effect=run),patch.object(m,'wait_ready',side_effect=ready):
            m.guest_update('0.1.0-preview.9',packages/'0.1.0-preview.9')
    except RuntimeError:
        pass
    else:raise AssertionError('Failed update reported success')
    state=json.loads(max(backup.iterdir(),key=lambda p:p.name).joinpath('state.json').read_text())
    assert state['status']=='rolled-back',state['status']
    verify_previous()
    checks.append(failure+'-failure-restores-schema5-credentials-data-program-and-session')

assert cli('0.1.0-preview.9').returncode==0
assert pg('SELECT max(version) FROM server_schema')=='6'
assert (m.APPLICATION/'current').resolve().name=='0.1.0-preview.9'
assert secrets_hash()==original
assert request('api/v1/management/session')[0]==200
assert request(f'api/v1/agents/{agent}/configuration',headers=bearer)[0]==200
assert (m.APPLICATION/'0.1.0-preview.8').is_dir()
assert json.loads((config/'installation.json').read_text())['request']['version']=='0.1.0-preview.9'
checks.append('actual-cli-preview8-to-preview9-schema5-to6-retains-account-agent-token-session-and-certificate')
count=len(list(backup.iterdir()));pid=Path('/tmp/native-server.pid').read_text()
assert cli('0.1.0-preview.9').returncode==0
assert len(list(backup.iterdir()))==count and Path('/tmp/native-server.pid').read_text()==pid
checks.append('repeat-version-is-no-op-without-service-restart-or-new-backup')
assert cli('0.1.0-preview.8').returncode!=0
assert pg('SELECT max(version) FROM server_schema')=='6'
assert Path('/tmp/native-server.pid').read_text()==pid
checks.append('downgrade-refused-without-changing-service-or-schema')
for directory in backup.iterdir():
    assert directory.stat().st_mode & 0o077 == 0
    state=json.loads((directory/'state.json').read_text())
    for name,digest in state['files'].items():
        with (directory/name).open('rb') as stream:assert hashlib.file_digest(stream,'sha256').hexdigest()==digest
checks.append('complete-private-program-database-config-data-and-unit-bundles-with-verified-hashes')
result={'success':True,'from':'0.1.0-preview.8','to':'0.1.0-preview.9','checks':checks,'realSystemdTested':False,'realProxmoxTested':False}
Path('/tmp/proxmox-update-result.json').write_text(json.dumps(result,indent=2))
print(json.dumps(result))
