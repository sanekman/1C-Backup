import hashlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import tarfile
import tempfile
import unittest
from unittest.mock import Mock, patch
import urllib.error


def load_tool(name):
    path = Path(__file__).resolve().parents[2] / 'tools' / (name + '.py')
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


guard = load_tool('check_storage_image_tag')
builder = load_tool('build_storage_distribution')
publisher = load_tool('publish_storage_distribution')


class StorageReleaseTests(unittest.TestCase):
    def token_response(self):
        response = io.BytesIO(b'{"token":"fixture-bearer"}')
        return response

    def test_existing_image_is_never_replaced(self):
        response = Mock(status=200)
        response.__enter__ = Mock(return_value=response)
        response.__exit__ = Mock(return_value=False)
        opener = Mock(side_effect=[self.token_response(), response])
        with self.assertRaisesRegex(RuntimeError, 'already exists'):
            guard.check_available('0.1.0-alpha.2', 'fixture-user', 'fixture-secret', opener)

    def test_only_confirmed_missing_tag_is_available(self):
        missing = urllib.error.HTTPError('https://ghcr.io/fixture', 404, 'Not Found', {}, None)
        opener = Mock(side_effect=[self.token_response(), missing])
        guard.check_available('0.1.0-alpha.2', 'fixture-user', 'fixture-secret', opener)
        self.assertEqual(opener.call_args.args[0].method, 'HEAD')

    def test_registry_access_and_server_errors_stop_publication(self):
        for status in [401, 403, 429, 500, 503]:
            with self.subTest(status=status):
                error = urllib.error.HTTPError('https://ghcr.io/fixture', status, 'Error', {}, None)
                opener = Mock(side_effect=[self.token_response(), error])
                with self.assertRaisesRegex(RuntimeError, 'could not confirm'):
                    guard.check_available('0.1.0-alpha.2', 'fixture-user', 'fixture-secret', opener)

    def test_invalid_version_never_contacts_registry(self):
        opener = Mock()
        with self.assertRaisesRegex(ValueError, 'Invalid'):
            guard.check_available('latest', 'fixture-user', 'fixture-secret', opener)
        opener.assert_not_called()

    def fixture(self, root):
        (root / 'tools').mkdir()
        (root / 'deploy/storage').mkdir(parents=True)
        (root / 'deploy/storage/VERSION').write_text('0.1.0-alpha.2\n')
        (root / 'artifacts').mkdir()
        return root / 'artifacts/SPBackup-Storage-0.1.0-alpha.2-linux-x64.tar.gz'

    def test_existing_local_package_is_not_overwritten_or_rebuilt(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            archive = self.fixture(root)
            archive.write_bytes(b'existing distribution')
            with patch.object(builder, '__file__', str(root / 'tools/build_storage_distribution.py')), \
                 patch.object(sys, 'argv', ['build_storage_distribution.py']), \
                 patch.object(builder.subprocess, 'run') as run:
                with self.assertRaisesRegex(FileExistsError, 'no overwrite'):
                    builder.main()
            run.assert_not_called()
            self.assertEqual(archive.read_bytes(), b'existing distribution')

    def package(self, root, runtime='10.0.13'):
        archive = self.fixture(root)
        config = {'runtimeOptions': {'includedFrameworks': [
            {'name': 'Microsoft.NETCore.App', 'version': runtime},
            {'name': 'Microsoft.AspNetCore.App', 'version': runtime}]}}
        with tarfile.open(archive, 'w:gz') as output:
            for name, contents in [('VERSION.txt', b'0.1.0-alpha.2\n'),
                ('SPBackup.StorageBridge.runtimeconfig.json', json.dumps(config).encode())]:
                member = tarfile.TarInfo('SPBackup-Storage/' + name)
                member.size = len(contents)
                output.addfile(member, io.BytesIO(contents))
        checksum = hashlib.sha256(archive.read_bytes()).hexdigest()
        archive.with_suffix('.gz.sha256').write_text(checksum + '  ' + archive.name + '\n')
        return archive

    def test_old_runtime_is_rejected_before_git_operations(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.package(root, '10.0.12')
            with patch.object(publisher, '__file__', str(root / 'tools/publish_storage_distribution.py')), \
                 patch.object(sys, 'argv', ['publish_storage_distribution.py']), \
                 patch.object(publisher.subprocess, 'check_output') as git:
                with self.assertRaisesRegex(ValueError, 'runtime'):
                    publisher.main()
            git.assert_not_called()

    def test_existing_distribution_branch_prevents_push(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            archive = self.package(root)
            before = archive.read_bytes()
            with patch.object(publisher, '__file__', str(root / 'tools/publish_storage_distribution.py')), \
                 patch.object(sys, 'argv', ['publish_storage_distribution.py']), \
                 patch.object(publisher.subprocess, 'check_output', return_value=b'existing-ref') as git:
                with self.assertRaisesRegex(RuntimeError, 'no overwrite'):
                    publisher.main()
            self.assertEqual(git.call_count, 1)
            self.assertEqual(git.call_args.args[0][1:3], ['ls-remote', '--heads'])
            self.assertEqual(archive.read_bytes(), before)


if __name__ == '__main__':
    unittest.main()
