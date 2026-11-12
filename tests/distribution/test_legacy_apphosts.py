"""The old updater and SCM must launch the new assemblies after a rename."""
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools"))
from legacy_apphosts import copy_legacy_apphosts


class LegacyApphostTests(unittest.TestCase):
    def test_alias_preserves_embedded_new_dll_and_signed_bytes(self):
        for windows in [True, False]:
            with self.subTest(windows=windows), tempfile.TemporaryDirectory() as temporary:
                directory = Path(temporary)
                extension = ".exe" if windows else ""
                source = directory / ("SPBackup.Agent" + extension)
                contents = b'MZ\0SPBackup.Agent.dll\0signed-payload'
                source.write_bytes(contents)
                source.chmod(0o755)
                copy_legacy_apphosts(directory, ["Agent"], windows=windows)
                alias = directory / ("OneCBackup.Agent" + extension)
                self.assertEqual(alias.read_bytes(), contents)
                self.assertEqual(alias.stat().st_mode, source.stat().st_mode)

    def test_missing_application_host_blocks_packaging(self):
        with tempfile.TemporaryDirectory() as temporary:
            with self.assertRaisesRegex(RuntimeError, "Missing application host"):
                copy_legacy_apphosts(Path(temporary), ["Agent"], windows=True)
