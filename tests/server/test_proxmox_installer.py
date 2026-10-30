"""No Proxmox commands are executed here; package safety and orchestration guards only."""
import importlib.util
import contextlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).resolve().parents[2] / 'deploy/server/create-proxmox-lxc.py'
spec = importlib.util.spec_from_file_location('proxmox_installer', SCRIPT)
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)


class ProxmoxInstallerTests(unittest.TestCase):
    def request(self, **fields):
        return dict(version='0.1.0-preview.5', address='https://192.168.10.120:7443/', name='Бухгалтерия', port=7443, **fields)

    def test_https_address_has_separate_name_and_accepts_standard_port(self):
        request = self.request()
        self.assertEqual(request, installer.validate_request(request))
        self.assertEqual(('https://backup.example.com:443/', 'backup.example.com'), installer.origin('https://backup.example.com', 443))
        for address in ['http://backup:7443', 'https://user:password@backup:7443', 'https://backup:7443/a',
                        'https://backup:7443/?password=secret', 'https://127.0.0.1:7443', 'https://0.0.0.0:7443',
                        'https://[::1]:7443', 'https://localhost:7443', 'https://backup:8443', 'https://bad_name:7443']:
            with self.subTest(address=address), self.assertRaises(ValueError):
                installer.origin(address, 7443)

    def test_request_rejects_injected_fields_versions_and_control_characters(self):
        for change in [dict(version='latest'), dict(version='../../other'), dict(port=0), dict(port=5432), dict(port=True),
                       dict(name='name\ncommand'), dict(name=' '), dict(password='secret')]:
            with self.subTest(change=change), self.assertRaises(ValueError):
                installer.validate_request({**self.request(), **change})

    def test_network_validates_router_and_rejects_shell_syntax(self):
        self.assertEqual('ip=dhcp', installer.network('dhcp', None))
        self.assertEqual('ip=192.168.1.120/24,gw=192.168.1.1', installer.network('192.168.1.120/24', '192.168.1.1'))
        for ip, gateway in [('dhcp', '192.168.1.1'), ('192.168.1.120/24', '192.168.2.1'),
                            ('192.168.1.120/24', '192.168.1.120'), ('192.168.1.0/24', '192.168.1.1'),
                            ('192.168.1.120/24', '192.168.1.0'), ('127.0.0.2/24', '127.0.0.1'),
                            ('dhcp,ip6=auto', None), ('192.168.1.120/24;touch /tmp/foreign', '192.168.1.1')]:
            with self.subTest(ip=ip, gateway=gateway), self.assertRaises(ValueError):
                installer.network(ip, gateway)

    def test_lxc_unit_does_not_require_nesting_and_limits_low_port_capability(self):
        unit = installer.service_unit(8443)
        self.assertIn('User=onec-backup-server', unit)
        self.assertIn('NoNewPrivileges=true', unit)
        self.assertIn('After=network-online.target postgresql@17-main.service', unit)
        self.assertNotIn('AmbientCapabilities=', unit)
        for directive in ['ProtectSystem=', 'PrivateTmp=', 'ReadWritePaths=']:
            self.assertNotIn(directive, unit)
        self.assertIn('AmbientCapabilities=CAP_NET_BIND_SERVICE', installer.service_unit(443))

    def test_manifest_does_not_allow_arbitrary_downloads_or_duplicate_packages(self):
        asset = dict(name=installer.ARCHIVE, url=f'https://github.com/{installer.REPOSITORY}/releases/download/server-v{installer.VERSION}/{installer.ARCHIVE}', bytes=1234, sha256='a' * 64)
        manifest = dict(version=installer.VERSION, runtime='10.0.13', sourceCommit='a' * 40, assets=[asset])
        self.assertEqual(asset, installer.package_asset(manifest, installer.VERSION))
        for change in [dict(runtime='other'), dict(sourceCommit='bad'), dict(assets=[asset, asset]),
                       dict(assets=[{**asset, 'url': 'https://foreign.example/server.tar.gz'}]),
                       dict(assets=[{**asset, 'bytes': 1024 ** 3}]), dict(assets=[{**asset, 'sha256': 'bad'}])]:
            with self.subTest(change=change), self.assertRaises(ValueError):
                installer.package_asset({**manifest, **change}, installer.VERSION)

    def test_archive_links_traversal_and_duplicate_files_cannot_overwrite_data(self):
        with tempfile.TemporaryDirectory() as work:
            root = Path(work)
            foreign = root / 'foreign'; foreign.write_text('preserve')
            for index, (name, kind) in enumerate([('../foreign', tarfile.REGTYPE), ('/foreign', tarfile.REGTYPE),
                    ('SP-Backup-Server/../../foreign', tarfile.REGTYPE), ('SP-Backup-Server/link', tarfile.SYMTYPE),
                    ('SP-Backup-Server/link', tarfile.LNKTYPE), ('SP-Backup-Server/pipe', tarfile.FIFOTYPE)]):
                archive = root / f'bad-{index}.tar.gz'
                with tarfile.open(archive, 'w:gz') as output:
                    member = tarfile.TarInfo(name); member.type = kind; member.linkname = str(foreign)
                    output.addfile(member)
                with self.subTest(name=name, kind=kind), self.assertRaises(ValueError):
                    installer.extract_package(archive, root / f'out-{index}', installer.VERSION)
                self.assertEqual('preserve', foreign.read_text())
            archive = root / 'duplicate.tar.gz'
            with tarfile.open(archive, 'w:gz') as output:
                for _ in range(2):
                    member = tarfile.TarInfo('SP-Backup-Server/a'); member.size = 1
                    output.addfile(member, io.BytesIO(b'x'))
            with self.assertRaises((ValueError, FileExistsError)):
                installer.extract_package(archive, root / 'duplicate', installer.VERSION)

    def test_bad_package_hash_never_activates_or_replaces_existing_program(self):
        with tempfile.TemporaryDirectory() as work:
            root = Path(work); package = root / 'package'; package.mkdir()
            data = b'corrupted archive'
            (package / installer.ARCHIVE).write_bytes(data)
            manifest = dict(version=installer.VERSION, runtime='10.0.13', sourceCommit='a' * 40, assets=[dict(
                name=installer.ARCHIVE, bytes=len(data), sha256='a' * 64,
                url=f'https://github.com/{installer.REPOSITORY}/releases/download/server-v{installer.VERSION}/{installer.ARCHIVE}')])
            (package / 'server-release.json').write_text(json.dumps(manifest))
            application = root / 'installed'; application.mkdir(); marker = application / 'foreign'; marker.write_text('preserve')
            with patch.object(installer, 'APPLICATION', application), self.assertRaises(ValueError):
                installer.install_package(self.request(), package)
            self.assertEqual('preserve', marker.read_text()); self.assertFalse((application / 'current').exists())

    def host(self, arguments, occupied=False, fail_install=False):
        args = installer.parser().parse_args(['--non-interactive', '--id', '120', '--template',
            'local:vztmpl/debian-13-standard_13.1-1_amd64.tar.zst', *arguments])
        calls = []
        requests = []
        def command(values, **kwargs):
            values = [str(v) for v in values]; calls.append(values)
            if values[:2] == ['pvesm', 'path']:
                return str(SCRIPT)  # Existing file; no template is downloaded in this test.
            if values[:2] == ['pct', 'push'] and values[3].endswith('request.json'):
                requests.append(json.loads(Path(values[3]).read_text(encoding='utf-8')))
            if fail_install and '--inside-container' in values:
                raise RuntimeError('Fixture installation failure')
            return ''
        def json_command(values):
            if values[:3] == ['pvesh', 'get', '/cluster/resources']:
                return [{'vmid': 120, 'node': 'other-node', 'type': 'qemu'}] if occupied else []
            if values[:3] == ['pvesh', 'get', '/cluster/nextid']:
                return 120
            if values[:2] == ['pct', 'exec']:
                return [{'addr_info': [{'local': '192.168.1.120', 'scope': 'global'}]}]
            raise AssertionError(values)
        with patch.object(installer.platform, 'machine', return_value='x86_64'), patch.object(installer.shutil, 'which', return_value='/tool'), \
                patch.object(installer, 'storage_names', return_value=['local-lvm', 'local']), \
                patch.object(installer, 'run', side_effect=command), patch.object(installer, 'json_command', side_effect=json_command):
            try:
                with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                    installer.host_create(args)
            except (ValueError, RuntimeError) as error:
                return calls, requests, error
        return calls, requests, False

    def test_occupied_id_on_another_cluster_node_is_never_adopted_or_started(self):
        calls, _, rejected = self.host([], occupied=True)
        self.assertTrue(rejected)
        self.assertFalse(any(c[0] in ('pct', 'pveam') for c in calls))

    def test_invalid_host_options_fail_before_any_ct_or_template_mutation(self):
        for arguments in [['--id', '0'], ['--bridge', 'vmbr0,ip=evil'], ['--hostname', 'bad\nname'], ['--port', '5432'],
                          ['--memory', '10'], ['--ip', '192.168.1.10/24', '--gateway', '10.0.0.1'],
                          ['--template', 'local:vztmpl/debian-12-standard_amd64.tar.zst']]:
            with self.subTest(arguments=arguments):
                calls, _, rejected = self.host(arguments)
                self.assertTrue(rejected)
                self.assertFalse(any(c[0] in ('pct', 'pveam') for c in calls))

    def test_fresh_ct_is_unprivileged_and_only_non_secret_json_crosses_pct(self):
        calls, requests, rejected = self.host(['--name', 'Бухгалтерия; резерв', '--port', '8443'])
        self.assertFalse(rejected)
        create = next(c for c in calls if c[:2] == ['pct', 'create'])
        self.assertEqual('1', create[create.index('--unprivileged') + 1])
        self.assertNotIn('--features', create)
        self.assertEqual([dict(version=installer.VERSION, address='https://192.168.1.120:8443/', name='Бухгалтерия; резерв', port=8443)], requests)
        self.assertFalse(any(c[:2] in (['pct', 'destroy'], ['pct', 'stop'], ['pct', 'set']) for c in calls))
        self.assertTrue(any(c[:2] == ['pct', 'exec'] and '--inside-container' in c for c in calls))

    def test_failed_installation_keeps_the_new_ct_and_never_stops_or_deletes_it(self):
        calls, requests, rejected = self.host([], fail_install=True)
        self.assertTrue(rejected)
        self.assertEqual(1, len(requests))
        self.assertTrue(any(c[:2] == ['pct', 'create'] for c in calls))
        self.assertFalse(any(c[:2] in (['pct', 'destroy'], ['pct', 'stop'], ['pct', 'set']) for c in calls))
        self.assertFalse(any(c[0] == 'apt-get' for c in calls))

    def test_help_is_available_without_root_or_proxmox(self):
        result = subprocess.run([sys.executable, str(SCRIPT), '--help'], capture_output=True, text=True,
                                encoding='utf-8', env=dict(os.environ, PYTHONIOENCODING='utf-8'))
        self.assertEqual(0, result.returncode)
        self.assertIn('--template', result.stdout)


if __name__ == '__main__':
    unittest.main()
