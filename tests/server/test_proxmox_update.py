"""Update orchestration never creates/reboots/destroys a CT; no Proxmox commands run here."""
import importlib.util
import io
import json
import os
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch
import hashlib

spec = importlib.util.spec_from_file_location('native_update', Path(__file__).resolve().parents[2] / 'deploy/server/create-proxmox-lxc.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ProxmoxUpdateTests(unittest.TestCase):
    def args(self, *extra):
        return module.parser().parse_args(['--update', '--id', '120', '--version', '0.1.0-preview.9', *extra])

    def test_host_updates_existing_ct_without_creation_reboot_or_reconfiguration(self):
        calls = []
        def run(args, **kwargs):
            calls.append(list(map(str, args)))
            return 'status: running' if args[:2] == ['pct', 'status'] else ''
        with patch.object(module.platform, 'machine', return_value='x86_64'), patch.object(module.shutil, 'which', return_value='/fixture'), \
             patch.object(module, 'json_command', return_value=[{'vmid': 120, 'type': 'lxc'}]), patch.object(module, 'run', side_effect=run):
            module.host_update(self.args())
        self.assertFalse(any(c[:2] in [['pct', 'create'], ['pct', 'destroy'], ['pct', 'start'], ['pct', 'reboot'], ['pct', 'set']] for c in calls))
        self.assertEqual('pct', calls[-1][0]); self.assertIn('--inside-container', calls[-1]); self.assertIn('--update', calls[-1]); self.assertIn('0.1.0-preview.9', calls[-1])
        pushed = next(c for c in calls if c[:2] == ['pct', 'push'])
        self.assertTrue(pushed[4].startswith('/root/sp-backup-update-'))
        self.assertIn('0600', pushed)

    def test_vm_missing_or_stopped_ct_is_refused_before_script_push(self):
        for resources, status in [([{'vmid':120,'type':'qemu'}], 'status: running'), ([], 'status: running'), ([{'vmid':120,'type':'lxc'}], 'status: stopped')]:
            with self.subTest(resources=resources,status=status), patch.object(module.platform,'machine',return_value='x86_64'), \
                 patch.object(module.shutil,'which',return_value='/fixture'), patch.object(module,'json_command',return_value=resources), \
                 patch.object(module,'run',return_value=status) as run:
                with self.assertRaises(ValueError): module.host_update(self.args())
                self.assertFalse(any(call.args[0][:2] == ['pct','push'] for call in run.call_args_list))

    def test_update_requires_explicit_existing_id(self):
        args = self.args(); args.id = None
        with patch.object(module.platform,'machine',return_value='x86_64'), patch.object(module.shutil,'which',return_value='/fixture'), patch.object(module,'run') as run:
            with self.assertRaises(ValueError): module.host_update(args)
            run.assert_not_called()

    def test_latest_selects_only_published_compatible_server_previews(self):
        def download(url, target, limit):
            target.write_text(json.dumps([{'tag_name':'v0.2.0-alpha.35'}, {'tag_name':'server-v0.1.0-preview.8'},
                {'tag_name':'server-v0.1.0-preview.9'}, {'tag_name':'server-v0.1.0-preview.99','draft':True}]))
        with patch.object(module,'download',side_effect=download):
            self.assertEqual('0.1.0-preview.9', module.latest_update_version())

    def test_equivalent_local_connection_strings_are_accepted(self):
        for value in ['Host=127.0.0.1;Database=onecbackup;Username=onecbackup',
                      ' user id = onecbackup ; DATABASE=onecbackup; Port=5432; Server=localhost; Pooling=false;Timeout=10',
                      'Server="127.0.0.1";Database="onecbackup";User Name=onecbackup;Application Name="fixture;name";Password="private;value"',
                      "Host=::1;Database=onecbackup;UID=onecbackup;PWD=\"quote\"\";private\""]:
            with self.subTest(value=value.split(';')[0]):
                host,port=module.update_database_target(value)
                self.assertIn(host,['localhost','127.0.0.1','::1']);self.assertEqual(5432,port)

    def test_unsafe_or_ambiguous_database_targets_are_rejected_without_secret_values(self):
        for value in ['Host=remote;Database=onecbackup;Username=onecbackup',
                      'Host=127.0.0.1;Port=6432;Database=onecbackup;Username=onecbackup',
                      'Host=127.0.0.1;Database=other;Username=onecbackup',
                      'Host=127.0.0.1;Database=onecbackup;Username=postgres',
                      'Host=127.0.0.1;Server=remote;Database=onecbackup;Username=onecbackup',
                      'Host=127.0.0.1;Database=onecbackup;Username=onecbackup;Service=other',
                      'Host=127.0.0.1;Database=onecbackup;Username=onecbackup;Password="private;value']:
            with self.subTest(target=value.split(';')[0]),self.assertRaises(ValueError) as error:
                module.update_database_target(value)
            self.assertNotIn('private',str(error.exception))

    def test_database_identity_must_match_the_cluster_being_backed_up(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);(root/'database-password.txt').write_text('private fixture credential')
            for started in ['123.456','different-cluster']:
                result=__import__('subprocess').CompletedProcess([],0,json.dumps({'database':'onecbackup','user':'onecbackup','started':started}),'')
                with patch.object(module,'CONFIG',root),patch.object(module,'json_command',return_value=[{'version':'17','cluster':'main','port':5432}]), \
                     patch.object(module,'sql',return_value='123.456'),patch.object(module.subprocess,'run',return_value=result) as run:
                    if started=='123.456':module.verify_update_database('localhost',5432)
                    else:
                        with self.assertRaises(ValueError):module.verify_update_database('localhost',5432)
                    self.assertNotIn('private fixture credential',' '.join(run.call_args.args[0]))

    @unittest.skipIf(os.name == 'nt', 'Linux filesystem and symlink semantics')
    def test_staging_verified_package_never_switches_current(self):
        version = '0.1.0-preview.9'
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); app=root/'app'; app.mkdir(); old=app/'0.1.0-preview.8';old.mkdir();(app/'current').symlink_to(old)
            packages=root/'packages';packages.mkdir();archive=packages/module.ARCHIVE
            with tarfile.open(archive,'w:gz') as tar:
                for name,data,mode in [('VERSION.txt',version.encode(),0o644),('SPBackup.Server',b'fixture executable',0o755)]:
                    info=tarfile.TarInfo('SP-Backup-Server/'+name);info.size=len(data);info.mode=mode;tar.addfile(info,io.BytesIO(data))
            digest=hashlib.sha256(archive.read_bytes()).hexdigest()
            (packages/'server-release.json').write_text(json.dumps({'version':version,'runtime':'10.0.13','sourceCommit':'a'*40,'assets':[
                {'name':module.ARCHIVE,'url':f'https://github.com/{module.REPOSITORY}/releases/download/server-v{version}/{module.ARCHIVE}',
                 'bytes':archive.stat().st_size,'sha256':digest}]}))
            with patch.object(module,'APPLICATION',app):
                staged=module.install_package({'version':version},packages,activate=False)
            self.assertEqual(old,(app/'current').resolve());self.assertEqual(app/version,staged)
            self.assertEqual(version,(staged/'VERSION.txt').read_text())

    @unittest.skipIf(os.name == 'nt', 'Linux symlinks')
    def test_linked_or_special_backup_tree_is_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary); (root/'link').symlink_to('/etc')
            with self.assertRaises(ValueError): module.reject_links(root)


if __name__ == '__main__': unittest.main()
