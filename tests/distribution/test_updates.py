import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

spec = importlib.util.spec_from_file_location('update_publisher', Path(__file__).resolve().parents[2] / 'tools/publish_windows_update.py')
publisher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(publisher)


class UpdatePublicationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.version = '0.1.0-preview.40'
        with zipfile.ZipFile(self.root / publisher.FILES[0], 'w') as archive:
            archive.writestr('1C-Backup/VERSION.txt', self.version + '\r\n')
        (self.root / publisher.FILES[2]).write_bytes(b'MZinstaller fixture')
        for name in [publisher.FILES[0], publisher.FILES[2]]:
            digest = hashlib.sha256((self.root / name).read_bytes()).hexdigest()
            (self.root / (name + '.sha256')).write_text(digest + '  ' + name + '\n')
        self.manifest = dict(SchemaVersion=1, Version=self.version, Platform='win-x64', InstallerKind='nsis',
                             InstallerFile=publisher.FILES[2], Size=(self.root / publisher.FILES[2]).stat().st_size,
                             Sha256=hashlib.sha256((self.root / publisher.FILES[2]).read_bytes()).hexdigest())
        self.save_manifest()

    def save_manifest(self):
        (self.root / 'update-manifest.json').write_text(json.dumps(self.manifest))

    def test_verified_distribution_can_be_published(self):
        self.assertEqual(self.version, publisher.verify_artifacts(self.root))

    def test_manifest_must_describe_actual_installer(self):
        self.manifest['Size'] += 1
        self.save_manifest()
        with self.assertRaisesRegex(ValueError, 'manifest'):
            publisher.verify_artifacts(self.root)

    def test_changed_payload_is_rejected_before_git_operations(self):
        (self.root / publisher.FILES[2]).write_bytes(b'MZchanged installer')
        with self.assertRaisesRegex(ValueError, 'Checksum'):
            publisher.verify_artifacts(self.root)

    def test_zip_version_must_match_manifest(self):
        self.manifest['Version'] = '0.1.0-preview.41'
        self.save_manifest()
        with self.assertRaisesRegex(ValueError, 'version'):
            publisher.verify_artifacts(self.root)


if __name__ == '__main__':
    unittest.main()
