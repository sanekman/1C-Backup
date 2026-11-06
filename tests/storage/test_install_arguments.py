import os
from pathlib import Path
import subprocess
import tempfile
import unittest


class InstallerArgumentTests(unittest.TestCase):
    repository = Path(__file__).resolve().parents[2]

    def test_options_require_values_before_privileged_or_network_operations(self):
        scripts = {
            'install-lxc.sh': ['--archive', '--endpoint', '--storage'],
            'install-from-github.sh': ['--endpoint', '--storage'],
            'create-proxmox-lxc.sh': ['--id', '--storage-path', '--template', '--rootfs-pool', '--bridge'],
        }
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            # Record any accidental download or Proxmox operation; never run the real tools.
            for name in ['git', 'pct', 'apt-get']:
                path = root / name
                path.write_text('#!/bin/sh\necho called >"$INSTALLER_CALL_LOG"\nexit 99\n')
                path.chmod(0o755)
            log = root / 'calls'
            environment = dict(os.environ, PATH=str(root) + os.pathsep + os.environ['PATH'],
                               INSTALLER_CALL_LOG=str(log))
            for script, options in scripts.items():
                for option in options:
                    for values in [[option], [option, ''], [option, '--storage']]:
                        with self.subTest(script=script, arguments=values):
                            result = subprocess.run(['bash', str(self.repository / 'deploy/storage' / script), *values],
                                                    env=environment, capture_output=True, text=True)
                            self.assertEqual(result.returncode, 2, result.stderr)
                            self.assertIn('Usage:', result.stdout + result.stderr)
                            self.assertNotIn('unbound variable', result.stderr)
                            self.assertFalse(log.exists())

    def test_proxmox_rejects_host_root_after_resolving_the_path(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name, content in [('id', '#!/bin/sh\necho 0\n'),
                                  ('pct', '#!/bin/sh\necho called >"$INSTALLER_CALL_LOG"\n')]:
                path = root / name
                path.write_text(content)
                path.chmod(0o755)
            alias = root / 'host-root'
            alias.symlink_to('/', target_is_directory=True)
            log = root / 'calls'
            environment = dict(os.environ, PATH=str(root) + os.pathsep + os.environ['PATH'],
                               INSTALLER_CALL_LOG=str(log))
            for data in ['/', '/tmp/..', str(alias) + '/']:
                with self.subTest(data=data):
                    result = subprocess.run(['bash', str(self.repository / 'deploy/storage/create-proxmox-lxc.sh'),
                                             '--id', '987654321', '--storage-path', data,
                                             '--template', 'local:vztmpl/debian-12-test.tar.zst'],
                                            env=environment, capture_output=True, text=True)
                    self.assertNotEqual(result.returncode, 0)
                    self.assertFalse(log.exists(), 'The host root reached pct')

    def test_help_does_not_require_root_or_downloads(self):
        for script in ['install-lxc.sh', 'install-from-github.sh', 'create-proxmox-lxc.sh']:
            with self.subTest(script=script):
                result = subprocess.run(['bash', str(self.repository / 'deploy/storage' / script), '--help'],
                                        capture_output=True, text=True)
                self.assertEqual(result.returncode, 0)
                self.assertIn('Usage:', result.stdout + result.stderr)

    def test_proxmox_passes_a_dedicated_directory_without_changing_ownership(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            data = root / 'backups'
            data.mkdir(mode=0o750)
            before = data.stat()
            for name, content in [('id', '#!/bin/sh\necho 0\n'),
                                  ('pct', '#!/bin/sh\nprintf "%s\\n" "$@" >>"$INSTALLER_CALL_LOG"\n')]:
                path = root / name
                path.write_text(content)
                path.chmod(0o755)
            log = root / 'calls'
            environment = dict(os.environ, PATH=str(root) + os.pathsep + os.environ['PATH'],
                               INSTALLER_CALL_LOG=str(log))
            result = subprocess.run(['bash', str(self.repository / 'deploy/storage/create-proxmox-lxc.sh'),
                                     '--id', '987654321', '--storage-path', str(data),
                                     '--template', 'local:vztmpl/debian-12-test.tar.zst'],
                                    env=environment, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn(str(data) + ',mp=/backups', log.read_text().splitlines())
            after = data.stat()
            self.assertEqual((before.st_mode, before.st_uid, before.st_gid),
                             (after.st_mode, after.st_uid, after.st_gid))


if __name__ == '__main__':
    unittest.main()
