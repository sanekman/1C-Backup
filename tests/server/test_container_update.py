import contextlib
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'tools'))
import update_server_container as updater


class ContainerUpdateTests(unittest.TestCase):
    def test_latest_release_uses_server_versions_not_agent_or_draft_or_creation_order(self):
        releases = [dict(tag_name='v0.2.0-alpha.35'), dict(tag_name='server-v0.1.0-preview.7'),
                    dict(tag_name='server-v0.1.0-preview.99', draft=True), dict(tag_name='server-v0.1.0-preview.10'),
                    dict(tag_name='server-v0.1.0-preview.8')]
        with patch.object(updater.urllib.request, 'urlopen', return_value=io.BytesIO(json.dumps(releases).encode())):
            self.assertEqual('server-v0.1.0-preview.10', updater.latest_version())

    def test_agent_branch_and_shell_text_cannot_select_an_update(self):
        with patch.object(updater, 'command') as run:
            for version in ['v0.2.0-alpha.35', 'feature/local-backup-agent', 'server-v0.1.0-preview.9; rm -rf /', '--all']:
                with self.assertRaises(updater.UpdateError):
                    updater.source_revision(Path('.'), version)
            run.assert_not_called()

    def test_fetch_does_not_force_tags_or_switch_the_working_branch(self):
        with patch.object(updater, 'command', side_effect=['', 'a' * 40 + '\n']) as run:
            self.assertEqual('a' * 40, updater.source_revision(Path('.'), 'server-v0.1.0-preview.9'))
            commands = [call.args[0] for call in run.call_args_list]
            self.assertEqual(['git', 'fetch', '--no-tags', 'origin', 'refs/tags/server-v0.1.0-preview.9:refs/tags/server-v0.1.0-preview.9'], commands[0])
            self.assertFalse(any('checkout' in cmd or '--force' in cmd for cmd in commands))

    def test_process_error_does_not_expose_private_output(self):
        with patch.object(updater.subprocess, 'run', return_value=subprocess.CompletedProcess([], 1, b'private password', b'secret connection')):
            with self.assertRaises(updater.UpdateError) as result:
                updater.command(['docker', 'compose', 'config'])
            self.assertNotIn('private', str(result.exception))
            self.assertNotIn('secret', str(result.exception))

    def test_schema_downgrade_is_rejected_before_stopping_or_dropping_anything(self):
        update = updater.ComposeUpdate.__new__(updater.ComposeUpdate)
        update.db, update.user, update.database = 'db-id', 'onecbackup', 'onecbackup'
        with patch.object(updater, 'command', side_effect=['public const int SchemaVersion = 5;', '6\n']) as run:
            with self.assertRaises(updater.UpdateError):
                update.check_schema(Path('.'), 'a' * 40)
            self.assertEqual(2, run.call_count)
            self.assertFalse(any('stop' in call.args[0] or 'dropdb' in call.args[0] for call in run.call_args_list))

    def test_changed_compose_settings_are_rejected_before_any_mutation(self):
        import copy
        environment = {'Server__ConnectionString': 'Host=db;Database=onecbackup',
                       'Server__DataDirectory': updater.DATA,
                       'Server__HttpsCertificatePath': '/run/secrets/server-certificate',
                       'Server__HttpsCertificatePasswordFile': '/run/secrets/certificate-password',
                       'Server__DatabasePasswordFile': '/run/secrets/database-password'}
        names = ['database-password', 'administrator-password', 'server-certificate', 'certificate-password']
        config = {'name': 'fixture', 'services': {'server': {'environment': environment,
                  'volumes': [{'source': 'server-data', 'target': updater.DATA, 'type': 'volume'}],
                  'ports': [{'target': 7443, 'published': '7443', 'host_ip': '127.0.0.1'}]}},
                  'volumes': {'server-data': {'name': 'fixture_server-data'}},
                  'secrets': {name: {'file': '/tmp/fixture/' + name} for name in names}}
        server = {'Image': 'sha256:fixture', 'State': {'Running': True},
                  'Config': {'Env': [key + '=' + value for key, value in environment.items()]},
                  'Mounts': [{'Type': 'volume', 'Destination': updater.DATA, 'Name': 'fixture_server-data'}] +
                            [{'Type': 'bind', 'Destination': '/run/secrets/' + name, 'Source': '/tmp/fixture/' + name} for name in names],
                  'NetworkSettings': {'Ports': {'7443/tcp': [{'HostIp': '127.0.0.1', 'HostPort': '7443'}]}}}
        db = {'State': {'Running': True}, 'Config': {'Image': 'postgres:17-alpine',
              'Env': ['POSTGRES_USER=onecbackup', 'POSTGRES_DB=onecbackup']}}
        for changed in ['environment', 'volume', 'secret', 'port', 'data-directory']:
            value = copy.deepcopy(config)
            if changed == 'environment': value['services']['server']['environment']['Server__ConnectionString'] = 'Host=db;Database=other'
            if changed == 'volume': value['volumes']['server-data']['name'] = 'other_volume'
            if changed == 'secret': value['secrets']['server-certificate']['file'] = '/tmp/other-cert'
            if changed == 'port': value['services']['server']['ports'][0]['published'] = '9999'
            live = copy.deepcopy(server)
            if changed == 'data-directory':
                value['services']['server']['environment']['Server__DataDirectory'] = '/unmounted-data'
                live['Config']['Env'] = [item.replace(updater.DATA, '/unmounted-data') for item in live['Config']['Env']]
            with self.subTest(changed=changed), patch.object(updater, 'command', return_value=json.dumps(value)) as run, \
                 patch.object(updater.ComposeUpdate, 'one_container', side_effect=['server-id', 'db-id']), \
                 patch.object(updater.ComposeUpdate, 'inspect', side_effect=[live, live, db]):
                with self.assertRaises(updater.UpdateError):
                    updater.ComposeUpdate(Path('/tmp/fixture/compose.yaml'))
                self.assertEqual(1, run.call_count)
                self.assertIn('config', run.call_args.args[0])

    def test_corrupt_bundle_prevents_destructive_database_restore(self):
        with tempfile.TemporaryDirectory() as temporary:
            update = updater.ComposeUpdate.__new__(updater.ComposeUpdate)
            update.backup = Path(temporary)
            update.state = {'files': {'database.dump': '0' * 64}}
            update.runtime = ['docker', 'compose']
            (update.backup / 'database.dump').write_bytes(b'corrupt')
            with patch.object(updater, 'command') as run:
                with self.assertRaises(updater.UpdateError):
                    update.rollback(None)
                self.assertFalse(any('dropdb' in call.args[0] for call in run.call_args_list))

    @unittest.skipIf(updater.fcntl is None, 'Linux file locking')
    def test_second_updater_is_rejected_and_lock_is_released(self):
        with tempfile.TemporaryDirectory() as temporary:
            update = updater.ComposeUpdate.__new__(updater.ComposeUpdate)
            update.compose_file = Path(temporary) / 'compose.yaml'
            update.project = 'fixture'
            with update.lock():
                with self.assertRaises(updater.UpdateError):
                    with update.lock():
                        pass
            with update.lock():
                pass

    def test_failed_build_removes_only_its_temporary_worktree(self):
        calls = []
        def run(args, **kwargs):
            calls.append(args)
            if args[:2] == ['docker', 'build']:
                raise updater.UpdateError('fixture build failure')
            return ''
        with patch.object(updater, 'command', side_effect=run):
            with self.assertRaises(updater.UpdateError):
                updater.build_image(Path('.'), 'a' * 40, 'server-v0.1.0-preview.9', 'fixture-image')
        self.assertEqual(['git', 'worktree', 'remove', '--force'], calls[-1][:4])
        self.assertEqual(calls[0][-2], calls[-1][-1])


if __name__ == '__main__':
    unittest.main()
