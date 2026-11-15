"""Validate tagged package jobs and an independent successful native fixture run."""
import json
import os
from pathlib import Path
import re
import subprocess

repository = os.environ['GITHUB_REPOSITORY']
tag = os.environ['RELEASE_TAG']
assert re.fullmatch(r'server-v\d+\.\d+\.\d+-preview\.\d+', tag)

def api(path):
    return json.loads(subprocess.check_output(['gh', 'api', f'repos/{repository}/{path}'], text=True))

source = api('git/ref/tags/' + tag)['object']
assert source['type'] == 'commit'
sha = source['sha']
run = api('actions/runs/' + str(int(os.environ['RELEASE_RUN'])))
assert run['path'] == '.github/workflows/server.yml' and run['head_sha'] == sha and run['event'] == 'push'
assert run['head_branch'] == tag and run['status'] == 'completed'
jobs = api(f"actions/runs/{run['id']}/jobs")['jobs']
for name in ['server (windows-latest, win-x64)', 'server (ubuntu-latest, linux-x64)']:
    matching = [job for job in jobs if job['name'] == name]
    assert len(matching) == 1 and matching[0]['conclusion'] == 'success', name
native = api('actions/runs/' + str(int(os.environ['NATIVE_RUN'])))
assert native['path'] == run['path'] and native['conclusion'] == 'success'
matching = [job for job in api(f"actions/runs/{native['id']}/jobs")['jobs'] if job['name'] == 'proxmox-installer']
assert len(matching) == 1 and matching[0]['conclusion'] == 'success'
# The independent run may correct the fixture, but cannot change shipped code/docs/tools.
comparison = api(f"compare/{sha}...{native['head_sha']}")
assert comparison['status'] in ['identical', 'ahead']
assert len(comparison['files']) < 300
assert all(file['filename'].startswith('tests/') and not file.get('previous_filename', 'tests/').startswith(('src/', 'deploy/', 'tools/')) for file in comparison['files'])
for commit in [sha, native['head_sha']]:
    subprocess.run(['git', 'fetch', '--no-tags', 'origin', commit], check=True)
# Fetch exact tag notes and report verifier. Publisher uses the recovery workflow version.
for name in ['docs/release-notes-server.md', 'tests/server/verify_results.py']:
    Path(name).write_bytes(subprocess.check_output(['git', 'show', sha + ':' + name]))
with open(os.environ['GITHUB_OUTPUT'], 'a') as stream:
    stream.write('source=' + sha + '\n')
print('Accepted tagged Windows/Linux packages and unchanged production native source:', sha)
