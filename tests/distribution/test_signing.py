import importlib.util
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

TOOLS = Path(__file__).resolve().parents[2] / 'tools'
sys.path.insert(0, str(TOOLS))
import windows_signing as signing


class SigningTests(unittest.TestCase):
    def setUp(self):
        self.environment = patch.dict(os.environ, {}, clear=True)
        self.environment.start()
        self.addCleanup(self.environment.stop)

    def test_unsigned_development_build_does_not_invoke_tools(self):
        with patch.object(signing.subprocess, 'run') as run:
            self.assertFalse(signing.sign_and_verify([]))
            run.assert_not_called()

    def test_required_signing_refuses_missing_certificate(self):
        os.environ['ONECBACKUP_REQUIRE_SIGNING'] = 'true'
        with self.assertRaisesRegex(RuntimeError, 'missing'):
            signing.sign_and_verify([])

    def test_thumbprint_cannot_inject_signing_arguments(self):
        os.environ['ONECBACKUP_SIGNING_THUMBPRINT'] = '/f other.pfx'
        with self.assertRaises(ValueError):
            signing.configured()

    def test_configured_signing_cannot_silently_fall_back_on_linux(self):
        os.environ['ONECBACKUP_SIGNING_THUMBPRINT'] = 'a' * 40
        with patch.object(signing.os, 'name', 'posix'):
            with self.assertRaisesRegex(RuntimeError, 'Windows machine'):
                signing.sign_and_verify([])

    def test_signing_selects_exact_certificate_and_checks_timestamp(self):
        from types import SimpleNamespace
        os.environ['ONECBACKUP_SIGNING_THUMBPRINT'] = 'a' * 40
        os.environ['ONECBACKUP_SIGNING_STORE'] = 'LocalMachine'
        with tempfile.TemporaryDirectory() as temporary:
            exe = Path(temporary) / 'program with spaces.exe'; exe.write_bytes(b'MZfixture')
            with patch.object(signing, 'os', SimpleNamespace(name='nt', environ=os.environ)), patch.object(signing.subprocess, 'run') as run:
                self.assertTrue(signing.sign_and_verify([exe]))
                commands = [call.args[0] for call in run.call_args_list]
                self.assertEqual(3, len(commands))
                self.assertIn('/sm', commands[0])
                self.assertEqual('A' * 40, commands[0][commands[0].index('/sha1') + 1])
                self.assertEqual('SHA256', commands[0][commands[0].index('/fd') + 1])
                self.assertEqual('SHA256', commands[0][commands[0].index('/td') + 1])
                self.assertEqual(exe.resolve(), Path(commands[0][-1]))
                self.assertEqual(['signtool.exe', 'verify', '/pa', '/all', '/tw', str(exe)], commands[1])
                self.assertIn('TimeStamperCertificate', commands[2][-1])
                self.assertEqual(str(exe), run.call_args_list[2].kwargs['env']['ONECBACKUP_VERIFY_SIGNED_FILE'])

    def test_untrusted_signature_never_reports_success(self):
        from types import SimpleNamespace
        os.environ['ONECBACKUP_SIGNING_THUMBPRINT'] = 'a' * 40
        with tempfile.TemporaryDirectory() as temporary:
            exe = Path(temporary) / 'program.exe'; exe.write_bytes(b'MZfixture')
            with patch.object(signing, 'os', SimpleNamespace(name='nt', environ=os.environ)), patch.object(signing.subprocess, 'run', side_effect=[None, subprocess.CalledProcessError(1, 'verify')]) as run:
                with self.assertRaises(subprocess.CalledProcessError): signing.sign_and_verify([exe])
                self.assertEqual(2, run.call_count)

    def test_signed_installer_hash_and_manifest_follow_signature(self):
        spec = importlib.util.spec_from_file_location('installer_signing_fixture', TOOLS / 'build_windows_installer.py')
        module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
        import hashlib
        import json
        import zipfile
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); archive = root / 'package.zip'
            with zipfile.ZipFile(archive, 'w') as output:
                output.writestr('1C-Backup/VERSION.txt', '0.1.0-test')
            archive.with_suffix('.zip.sha256').write_text(hashlib.sha256(archive.read_bytes()).hexdigest())
            exe = root / '1C-Backup-0.1.0-test-windows-x64-setup.exe'
            def compile_fixture(*args, **kwargs): exe.write_bytes(b'MZfixture')
            def sign_fixture(files):
                self.assertEqual([exe], files)
                exe.write_bytes(exe.read_bytes() + b'fixture signature')
                return True
            with patch.object(module.subprocess, 'run', side_effect=compile_fixture), patch.object(signing, 'sign_and_verify', side_effect=sign_fixture):
                module.build(archive, 'fixture compiler', 'nsis')
            manifest = json.loads((root / 'update-manifest.json').read_text())
            self.assertEqual(hashlib.sha256(exe.read_bytes()).hexdigest(), manifest['Sha256'])
            self.assertEqual(exe.stat().st_size, manifest['Size'])

    def test_signing_failure_prevents_update_manifest(self):
        spec = importlib.util.spec_from_file_location('installer_failure_fixture', TOOLS / 'build_windows_installer.py')
        module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
        import hashlib
        import zipfile
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); archive = root / 'package.zip'
            with zipfile.ZipFile(archive, 'w') as output: output.writestr('1C-Backup/VERSION.txt', '0.1.0-test')
            archive.with_suffix('.zip.sha256').write_text(hashlib.sha256(archive.read_bytes()).hexdigest())
            exe = root / '1C-Backup-0.1.0-test-windows-x64-setup.exe'
            with patch.object(module.subprocess, 'run', side_effect=lambda *args, **kwargs: exe.write_bytes(b'MZfixture')), patch.object(signing, 'sign_and_verify', side_effect=RuntimeError('Untrusted certificate')):
                with self.assertRaisesRegex(RuntimeError, 'Untrusted'):
                    module.build(archive, 'fixture compiler', 'nsis')
            self.assertFalse((root / 'update-manifest.json').exists())


if __name__ == '__main__':
    unittest.main()
