"""Update orchestration never creates/reboots/destroys a CT; no Proxmox commands run here."""
import importlib.util
import io
import json
import os
from pathlib import Path
import tarfile
import tempfile
import subprocess
import sys
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
        def step(label, args, **kwargs):
            calls.append(list(map(str, args)))
            if args[:2] == ['pct', 'status']: return 'status: running'
            if 'sha256sum' in args:
                return hashlib.sha256(Path(module.__file__).read_bytes()).hexdigest() + '  ' + args[-1]
            return ''
        with patch.object(module.platform, 'machine', return_value='x86_64'), patch.object(module.shutil, 'which', return_value='/fixture'), \
             patch.object(module, 'json_command', return_value=[{'vmid': 120, 'type': 'lxc'}]), patch.object(module, 'host_update_step', side_effect=step):
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
                 patch.object(module,'host_update_step',return_value=status) as run:
                with self.assertRaises(ValueError): module.host_update(self.args())
                self.assertFalse(any(call.args[1][:2] == ['pct','push'] for call in run.call_args_list))

    def test_update_requires_explicit_existing_id(self):
        args = self.args(); args.id = None
        with patch.object(module.platform,'machine',return_value='x86_64'), patch.object(module.shutil,'which',return_value='/fixture'), patch.object(module,'host_update_step') as run:
            with self.assertRaises(ValueError): module.host_update(args)
            run.assert_not_called()

    def test_transfer_failure_and_mismatched_hash_never_launch_the_guest(self):
        for failure in ['directory', 'push', 'hash']:
            calls=[]
            def step(label,args,**kwargs):
                calls.append(args)
                if args[:2]==['pct','status']:return 'status: running'
                if failure=='directory' and 'install' in args:raise RuntimeError('directory failure 129')
                if failure=='push' and args[:2]==['pct','push']:raise RuntimeError('push failure 129')
                if 'sha256sum' in args:return '0'*64+'  '+args[-1]
                return ''
            with self.subTest(failure=failure),patch.object(module.platform,'machine',return_value='x86_64'), \
                 patch.object(module.shutil,'which',return_value='/fixture'), \
                 patch.object(module,'json_command',return_value=[{'vmid':120,'type':'lxc'}]), \
                 patch.object(module,'host_update_step',side_effect=step):
                with self.assertRaises((RuntimeError,ValueError)):module.host_update(self.args())
                self.assertFalse(any('--inside-container' in call for call in calls))
                self.assertEqual(1,sum('install' in call for call in calls))

    def test_failed_pct_step_identifies_the_stage_and_preserves_exit_code(self):
        with self.assertRaisesRegex(RuntimeError,'fixture transfer.*129'):
            module.host_update_step('fixture transfer',[sys.executable,'-c','import sys;sys.exit(129)'])

    @unittest.skipIf(os.name == 'nt', 'Linux PTY semantics used by lxc-attach')
    def test_pct_step_has_no_tty_even_when_started_from_an_interactive_console(self):
        import pty
        master,slave=pty.openpty()
        try:
            child="import os;assert not any(os.isatty(fd) for fd in (0,1,2));print('no-tty: OK')"
            code=("import importlib.util,sys;spec=importlib.util.spec_from_file_location('updater',"+repr(module.__file__)+
                  ");m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m);"
                  "m.host_update_step('fixture console',[sys.executable,'-c',"+repr(child)+"])")
            result=subprocess.run([sys.executable,'-c',code],stdin=slave,stdout=slave,stderr=slave,timeout=10)
            self.assertEqual(0,result.returncode)
            self.assertIn(b'no-tty: OK',os.read(master,8192))
        finally:
            os.close(slave);os.close(master)

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
                host,port,database=module.update_database_target(value)
                self.assertIn(host,['localhost','127.0.0.1','::1']);self.assertEqual(5432,port);self.assertEqual('onecbackup',database)

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

    def test_utf8_copy_name_is_selected_but_arbitrary_names_are_rejected(self):
        name='onecbackup_utf8_20261102_034056'
        self.assertEqual(('127.0.0.1',5432,name),module.update_database_target(
            f'Host=127.0.0.1;Database={name};Username=onecbackup'))
        for invalid in ['postgres','template1','other','onecbackup_utf8_',
                        'onecbackup_utf8_20261102_034056_suffix',"onecbackup_utf8_20261102_034056'--"]:
            with self.subTest(name=invalid),self.assertRaises(ValueError):
                module.update_database_target(f'Host=127.0.0.1;Database={invalid};Username=onecbackup')

    def metadata(self, name='onecbackup'):
        return {'name':name,'owner':'onecbackup','encoding':'UTF8','collate':'C','ctype':'C','bytes':1234}

    def test_database_identity_must_match_the_cluster_being_backed_up(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);(root/'database-password.txt').write_text('private fixture credential')
            for name in ['onecbackup','onecbackup_utf8_20261102_034056']:
                for mismatch in ['none','cluster','database','schema']:
                    observed={'database':name,'user':'onecbackup','started':'123.456','schema':True}
                    if mismatch=='cluster':observed['started']='different-cluster'
                    if mismatch=='database':observed['database']='other'
                    if mismatch=='schema':observed['schema']=False
                    result=__import__('subprocess').CompletedProcess([],0,json.dumps(observed),'')
                    with self.subTest(name=name,mismatch=mismatch),patch.object(module,'CONFIG',root), \
                         patch.object(module,'json_command',return_value=[{'version':'17','cluster':'main','port':5432}]), \
                         patch.object(module,'sql',side_effect=[json.dumps(self.metadata(name)),'123.456']), \
                         patch.object(module.subprocess,'run',return_value=result) as run:
                        if mismatch=='none':self.assertEqual(self.metadata(name),module.verify_update_database('localhost',5432,name))
                        else:
                            with self.assertRaises(ValueError):module.verify_update_database('localhost',5432,name)
                        self.assertNotIn('private fixture credential',' '.join(run.call_args.args[0]))
                        argv=run.call_args.args[0];self.assertEqual(name,argv[argv.index('-d')+1])

    def test_missing_database_wrong_owner_and_non_utf8_are_refused_before_connection(self):
        for metadata in [None,{**self.metadata(),'owner':'postgres'},{**self.metadata(),'encoding':'SQL_ASCII'}]:
            with self.subTest(metadata=metadata),patch.object(module,'json_command',return_value=[{'version':'17','cluster':'main','port':5432}]), \
                 patch.object(module,'sql',return_value=json.dumps(metadata)),patch.object(module.subprocess,'run') as run:
                with self.assertRaises(ValueError):module.verify_update_database('127.0.0.1',5432,'onecbackup')
                run.assert_not_called()

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
