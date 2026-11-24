"""Seed a newly installed disposable LXC with one agent, plan and run."""
from datetime import datetime, timedelta, timezone
import html
import http.cookiejar
import json
from pathlib import Path
import re
import ssl
import urllib.parse
import urllib.request
import uuid

config = Path('/etc/onec-backup-server')
base = 'https://127.0.0.1:8443/'
tls = ssl.create_default_context(cafile=str(config / 'ca.crt'))
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPSHandler(context=tls),
                                     urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))


def request(path, payload=None, headers=None, method=None, form=False):
    data = (urllib.parse.urlencode(payload) if form else json.dumps(payload)).encode() if payload is not None else None
    headers = dict(headers or {})
    if data is not None:
        headers['Content-Type'] = 'application/x-www-form-urlencoded' if form else 'application/json'
    with opener.open(urllib.request.Request(base + path, data=data, headers=headers, method=method), timeout=10) as response:
        return response.read().decode()


anti = html.unescape(re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', request('login')).group(1))
request('session/login', {'name': 'admin', 'password': (config / 'administrator-password.txt').read_text().strip(),
                         '__RequestVerificationToken': anti}, form=True)
csrf = {'X-CSRF-TOKEN': json.loads(request('api/v1/management/session'))['csrfToken']}
folder = json.loads(request('api/v1/management/folders', {'name': 'Recovery drill'}, csrf))
agent = str(uuid.uuid4())
job = str(uuid.uuid4())
invite = json.loads(request('api/v1/management/invitations', {'name': 'Recovery drill', 'agentId': agent, 'folderId': folder['id']}, csrf))
enrollment = json.loads(request('api/v1/enrollment', {'invitationCode': invite['code'], 'agentId': agent, 'name': 'Recovery agent'}))
bearer = {'Authorization': 'Bearer ' + enrollment['token']}
name = 'Recovery documents'
prefix = f'api/v1/agents/{agent}'
plan = json.loads(request(f'api/v1/management/agents/{agent}/configuration', {'agentId': agent, 'protocolVersion': 1, 'jobs': [
    {'id': job, 'name': name, 'source': r'C:\RecoveryTest', 'destinationDirectory': r'D:\RecoveryCopies'}]},
    {**csrf, 'If-Match': '"1"'}, 'PUT'))
assert plan['revision'] == 2
now = datetime.now(timezone.utc)
request(prefix + '/monitoring', {'sentUtc': now.isoformat(), 'agentVersion': 'fixture', 'jobs': [
    {'jobId': job, 'name': name, 'sourceKind': 'FilesAndFolders', 'enabled': True, 'schedule': 'Manual', 'destinationCount': 1}], 'pendingRuns': 0},
    bearer, 'PUT')
request(prefix + '/runs', {'runId': str(uuid.uuid4()), 'jobId': job, 'jobName': name, 'startedUtc': (now-timedelta(minutes=1)).isoformat(),
    'finishedUtc': now.isoformat(), 'status': 'Succeeded', 'warningCount': 0, 'incomplete': False,
    'destinations': [{'index': 0, 'succeeded': True, 'bytes': 256, 'incomplete': False}]}, bearer)
print('Seeded a folder, agent, configuration and run over verified HTTPS.')
