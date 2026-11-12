"""Exercise updater recovery against the disposable real Compose stack."""
import json
import os
import subprocess
import sys
import time
from unittest.mock import patch
import uuid


def verify(repository, root, project, environment, request):
    sys.path.insert(0, str(repository / 'tools'))
    import update_server_container as updater
    checks = []
    candidate = 'spbackup-update-smoke:' + uuid.uuid4().hex
    baseline = 'spbackup-update-baseline:' + uuid.uuid4().hex
    with patch.dict(os.environ, environment):
        update = updater.ComposeUpdate(repository / 'deploy/server/compose.yaml', project=project, backup_root=root / 'updates')
        original_db = update.db
        updater.command(['docker', 'tag', update.old_image, baseline])
        subprocess.run(['docker', 'build', '-t', candidate, '-'], input=('FROM ' + baseline + '\nLABEL updater_smoke="' + uuid.uuid4().hex + '"\n').encode(), check=True, capture_output=True)
        try:
            version = 'server-v0.0.0-preview.9' + str(int(time.time()))
            updater.command(['git', 'tag', version], cwd=repository)
            try:
                cli = ['sh', str(repository / 'deploy/server/update.sh'), '--offline', '--version', version,
                       '--project-name', project, '--backup-directory', str(root / 'updates')]
                updater.command(cli, cwd=repository)
                completed = sorted((root / 'updates').iterdir())
                assert len(completed) == 1
                update.backup = completed[0]
                current_container = update.one_container('server')
                updater.command(cli, cwd=repository)
                assert update.one_container('server') == current_container
                assert len(list((root / 'updates').iterdir())) == 1
                checks.append('one-command-cli-resolves-tag-builds-isolated-source-and-second-run-is-no-op')
            finally:
                updater.command(['git', 'tag', '-d', version], cwd=repository)
            assert json.loads((update.backup / 'state.json').read_text())['status'] == 'updated'
            assert update.one_container('server') != update.server
            assert update.one_container('db') == original_db
            assert request('api/v1/management/session')[0] == 200
            checks.append('update-recreates-only-server-and-preserves-database-and-session')
            for name in ['previous-image.tar', 'database.dump', 'server-data.tar']:
                assert (update.backup / name).stat().st_size > 0
            assert update.backup.stat().st_mode & 0o077 == 0
            checks.append('private-complete-image-database-data-config-and-secret-bundle')
            for failure in ['migration', 'readiness']:
                restore = updater.ComposeUpdate(repository / 'deploy/server/compose.yaml', project=project, backup_root=root / 'updates')
                expected_image = restore.old_image
                real_command = updater.command
                real_ready = restore.ready
                calls = 0
                def damage():
                    real_command(['docker', 'exec', restore.db, 'psql', '-U', restore.user, '-d', restore.database, '-c',
                                  'CREATE TABLE updater_failed_marker(id int); INSERT INTO server_schema(version) VALUES(999)'])
                    real_command(['docker', 'run', '--rm', '--network', 'none', '--user', '0', '--mount',
                                  'type=volume,src=' + restore.volume + ',dst=/snapshot', '--entrypoint', 'sh', restore.old_image,
                                  '-c', 'echo changed > /snapshot/updater-failed-marker'])
                def fail_command(args, **kwargs):
                    if failure == 'migration' and args[-1] == '--migrate':
                        damage()
                        raise updater.UpdateError('Injected migration error')
                    return real_command(args, **kwargs)
                def fail_ready(context, timeout=60):
                    nonlocal calls
                    calls += 1
                    if failure == 'readiness' and calls == 2:
                        damage()
                        raise updater.UpdateError('Injected readiness error')
                    return real_ready(context, timeout)
                with patch.object(updater, 'command', side_effect=fail_command), patch.object(restore, 'ready', side_effect=fail_ready):
                    try:
                        with restore.lock():
                            restore.update(candidate, 'server-v0.0.0-preview.9002', '2' * 40)
                    except updater.UpdateError:
                        pass
                    else:
                        raise AssertionError('Failed update reported success')
                assert json.loads((restore.backup / 'state.json').read_text())['status'] == 'rolled-back'
                container = restore.one_container('server')
                assert restore.inspect(container)['Image'] == expected_image
                assert restore.one_container('db') == original_db
                assert real_command(['docker', 'exec', restore.db, 'psql', '-U', restore.user, '-d', restore.database, '-At', '-c',
                                     "SELECT to_regclass('updater_failed_marker')"]).strip() == ''
                real_command(['docker', 'exec', container, 'test', '!', '-e', '/var/lib/onec-backup-server/updater-failed-marker'])
                assert request('api/v1/management/session')[0] == 200
                checks.append(failure + '-failure-restores-image-schema-data-and-session-with-failure-result')
        finally:
            for tag in [candidate, baseline]:
                subprocess.run(['docker', 'image', 'rm', tag], capture_output=True)
    return checks
