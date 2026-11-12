"""Actual Debian/PG/package update; controlled failures, systemctl recorder, no real Proxmox."""
import hashlib
import html
import http.cookiejar
import importlib.util
import json
from pathlib import Path
import re
import ssl
import socket
import threading
import os
import pwd
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
database_name="onecbackup"

def cli(version, directory=None):
    return subprocess.run(['python3','/setup/install.py','--inside-container','--update','--version',version,
                           '--package-directory',str(directory or packages/version)],capture_output=True)

def pg(statement, database=None):
    return subprocess.check_output(['runuser','-u','postgres','--','psql','-X','-qAt','-d',database or database_name,'-c',statement],text=True).strip()

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
# Simulate an equivalent connection string, including quoted delimiters and explicit port.
settings=json.loads((config/'server.json').read_text())
connection='User ID=onecbackup; DATABASE=onecbackup; Port=5432; SERVER=localhost; Pooling=false;Timeout=10;Application Name="Proxmox; update";Password="ignored;fixture"'
settings['Server']['ConnectionString']=connection
(config/'server.json').write_text(json.dumps(settings))
subprocess.run(['systemctl','stop','onec-backup-server'],check=True)
subprocess.run(['systemctl','start','onec-backup-server'],check=True)
m.wait_ready(8443)
checks.append('equivalent-connection-string-validated-against-the-same-local-cluster')

# Simulate the actual UTF8 repair: renamed application DB alongside an old SQL_ASCII DB.
subprocess.run(['systemctl','stop','onec-backup-server'],check=True)
database_name='onecbackup_utf8_20261102_034056'
m.sql('ALTER DATABASE onecbackup RENAME TO '+database_name)
m.sql("CREATE DATABASE onecbackup OWNER onecbackup TEMPLATE template0 ENCODING 'SQL_ASCII' LC_COLLATE 'C' LC_CTYPE 'C'")
pg("CREATE TABLE untouched_original(marker text); INSERT INTO untouched_original VALUES('old-database-retained')",database='onecbackup')
connection=connection.replace('DATABASE=onecbackup;', 'DATABASE='+database_name+';')
settings['Server']['ConnectionString']=connection
(config/'server.json').write_text(json.dumps(settings))
subprocess.run(['systemctl','start','onec-backup-server'],check=True)
m.wait_ready(8443)
assert secrets_hash()==original
checks.append('renamed-utf8-application-database-alongside-old-sql-ascii-database')

# Every unsafe target must be rejected before stopping the running app or making a kit.
rejected_name='onecbackup_utf8_20261102_000001'
for invalid in ['missing','owner','encoding','schema']:
    if invalid!='missing':
        owner='postgres' if invalid=='owner' else 'onecbackup'
        encoding='SQL_ASCII' if invalid=='encoding' else 'UTF8'
        m.sql(f"CREATE DATABASE {rejected_name} OWNER {owner} TEMPLATE template0 ENCODING '{encoding}' LC_COLLATE 'C' LC_CTYPE 'C'")
    settings['Server']['ConnectionString']=connection.replace(database_name,rejected_name)
    (config/'server.json').write_text(json.dumps(settings))
    before=len(list(backup.iterdir()));pid=Path('/tmp/native-server.pid').read_text()
    try:
        assert cli('0.1.0-preview.9').returncode!=0
        assert len(list(backup.iterdir()))==before and Path('/tmp/native-server.pid').read_text()==pid
        assert pg('SELECT max(version) FROM server_schema')=='5'
    finally:
        settings['Server']['ConnectionString']=connection
        (config/'server.json').write_text(json.dumps(settings))
        if invalid!='missing':m.sql('DROP DATABASE '+rejected_name)
checks.append('missing-owner-encoding-and-schema-refusals-without-service-stop-or-new-backup')

def verify_original_database():
    assert pg('SELECT marker FROM untouched_original',database='onecbackup')=='old-database-retained'
    assert pg('SHOW server_encoding',database='onecbackup')=='SQL_ASCII'
    assert pg("SELECT to_regclass('public.server_schema')",database='onecbackup')==''

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
    verify_original_database()
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
    assert state['database']['name']==database_name
    verify_previous()
    checks.append(failure+'-failure-restores-schema5-credentials-data-program-and-session')

# Install the root-owned broker once, then exercise the real socket protocol.
assert subprocess.run(['python3','/setup/install.py','--inside-container','--enable-web-updates'],capture_output=True).returncode==0
account=pwd.getpwnam('onec-backup-server')
assert m.WEB_UPDATE_SCRIPT.stat().st_uid==0 and m.WEB_UPDATE_SCRIPT.stat().st_mode & 0o077==0
assert m.WEB_UPDATE_HOME.stat().st_uid==0 and m.WEB_UPDATE_HOME.stat().st_mode & 0o077==0
assert subprocess.run(['runuser','-u',account.pw_name,'--','test','-w',str(m.WEB_UPDATE_SCRIPT)]).returncode!=0
assert subprocess.run(['runuser','-u',account.pw_name,'--','test','-r',str(m.WEB_UPDATE_HOME)]).returncode!=0
units=list(Path('/etc/systemd/system').glob('onec-backup-server-update*'))
assert len(units)==4
subprocess.run(['systemd-analyze','verify',*map(str,units)],check=True)
checks.append('one-time-broker-install-root-only-code-state-and-valid-systemd-units')

# Emulate socket activation only; each control handler and update worker is real.
listener=socket.socket(socket.AF_UNIX,socket.SOCK_STREAM)
listener.bind(m.WEB_UPDATE_SOCKET);os.chown(m.WEB_UPDATE_SOCKET,0,account.pw_gid);os.chmod(m.WEB_UPDATE_SOCKET,0o660);listener.listen()
def rpc(payload):
    failures=[]
    def handler():
        connection,_=listener.accept()
        with connection:
            result=subprocess.run(['python3',str(m.WEB_UPDATE_SCRIPT),'--web-update-control'],stdin=connection,stdout=connection,stderr=subprocess.PIPE)
            if result.returncode:failures.append(result.returncode)
    thread=threading.Thread(target=handler);thread.start()
    code="import socket; s=socket.socket(socket.AF_UNIX); s.settimeout(15); s.connect("+repr(m.WEB_UPDATE_SOCKET)+"); s.sendall("+repr(json.dumps(payload).encode()+b'\n')+"); print(s.makefile('rb').readline().decode())"
    result=subprocess.run(['runuser','-u',account.pw_name,'--','python3','-c',code],capture_output=True,text=True,timeout=20)
    thread.join(timeout=20);assert not thread.is_alive() and not failures and result.returncode==0
    return json.loads(result.stdout)
assert rpc({'command':'status'})['phase']=='idle'
assert rpc({'command':'start','version':'https://other/program'})['phase']=='idle'
assert rpc({'command':'check'})['phase']=='checking'
m.web_update_check()
assert rpc({'command':'status'})['phase']=='available'
assert rpc({'command':'start','version':'0.1.0-preview.9'})['phase']=='queued'
assert rpc({'command':'start','version':'0.1.0-preview.9'})['phase']=='queued'
calls=Path('/tmp/systemctl-calls.txt').read_text().splitlines()
assert calls.count('start --no-block onec-backup-server-update-run.service')==1
checks.append('unprivileged-socket-peer-check-start-and-duplicate-request-with-fixed-root-units')
m.web_update_run()
state=rpc({'command':'status'})
assert state['phase']=='updated' and state['currentVersion']=='0.1.0-preview.9',state['phase']
assert state.get('backupDirectory') and 'log' not in state
listener.close();Path(m.WEB_UPDATE_SOCKET).unlink()
checks.append('separate-root-worker-updates-published-package-and-retains-result-after-app-restart')
assert pg('SELECT max(version) FROM server_schema')=='6'
assert (m.APPLICATION/'current').resolve().name=='0.1.0-preview.9'
assert secrets_hash()==original
assert json.loads((config/'server.json').read_text())['Server']['ConnectionString']==connection
assert request('api/v1/management/session')[0]==200
assert request(f'api/v1/agents/{agent}/configuration',headers=bearer)[0]==200
assert (m.APPLICATION/'0.1.0-preview.8').is_dir()
assert json.loads((config/'installation.json').read_text())['request']['version']=='0.1.0-preview.9'
verify_original_database()
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
    assert state['database']['name'] in ['onecbackup',database_name]
    for name,digest in state['files'].items():
        with (directory/name).open('rb') as stream:assert hashlib.file_digest(stream,'sha256').hexdigest()==digest
verify_original_database()
checks.append('original-sql-ascii-database-untouched-by-update-and-both-rollbacks')
checks.append('complete-private-program-database-config-data-and-unit-bundles-with-verified-hashes')
result={'success':True,'from':'0.1.0-preview.8','to':'0.1.0-preview.9','checks':checks,'realSystemdTested':False,'realProxmoxTested':False}
Path('/tmp/proxmox-update-result.json').write_text(json.dumps(result,indent=2))
print(json.dumps(result))
