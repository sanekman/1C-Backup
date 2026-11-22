"""Validate package jobs and the corresponding native acceptance run."""
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

candidate = os.environ.get('CANDIDATE_VERSION')
run = api('actions/runs/' + str(int(os.environ['RELEASE_RUN'])))
assert run['path'] == '.github/workflows/server.yml' and run['status'] == 'completed'
native = api('actions/runs/' + str(int(os.environ['NATIVE_RUN'])))
assert native['path'] == run['path'] and native['conclusion'] == 'success'
sha = run['head_sha']
jobs = api(f"actions/runs/{run['id']}/jobs")['jobs']

def successful_job(jobs, name):
    matching = [job for job in jobs if job['name'] == name]
    assert len(matching) == 1 and matching[0]['conclusion'] == 'success', name


for name in ['server (windows-latest, win-x64)', 'server (ubuntu-latest, linux-x64)']:
    successful_job(jobs, name)
native_jobs = api(f"actions/runs/{native['id']}/jobs")['jobs']
if candidate:
    assert tag == 'server-v' + candidate and run['event'] == native['event'] == 'workflow_dispatch'
    assert run['conclusion'] == 'success' and run['head_branch'] == native['head_branch']
    successful_job(jobs, 'proxmox-installer')
    successful_job(native_jobs, 'lxc-clone-lab')
else:
    source = api('git/ref/tags/' + tag)['object']
    assert source['type'] == 'commit' and source['sha'] == sha
    assert run['event'] == 'push' and run['head_branch'] == tag
    successful_job(native_jobs, 'proxmox-installer')
    # The independent run may correct only the fixture.
    comparison = api(f"compare/{sha}...{native['head_sha']}")
    assert comparison['status'] in ['identical', 'ahead'] and len(comparison['files']) < 300
    assert all(file['filename'].startswith('tests/') and file.get('previous_filename', 'tests/').startswith('tests/') for file in comparison['files'])
for commit in [sha, native['head_sha']]:
    subprocess.run(['git', 'fetch', '--no-tags', 'origin', commit], check=True)
# Fetch exact tag notes and report verifier. Publisher uses the recovery workflow version.
for name in ['docs/release-notes-server.md', 'tests/server/verify_results.py']:
    Path(name).write_bytes(subprocess.check_output(['git', 'show', sha + ':' + name]))
with open(os.environ['GITHUB_OUTPUT'], 'a') as stream:
    stream.write('source=' + sha + '\n')
print('Accepted Windows/Linux package source:', sha)
