import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("distribution", Path(__file__).resolve().parents[2] / "tools/build_windows_distribution.py")
distribution = importlib.util.module_from_spec(spec)
spec.loader.exec_module(distribution)


class DistributionVersionTests(unittest.TestCase):
    def check_version(self, desktop, agent, tag=None):
        with tempfile.TemporaryDirectory() as directory, patch.dict(distribution.os.environ, {}, clear=True):
            publish = Path(directory)
            for project, version in [("SPBackup.Desktop", desktop), ("SPBackup.Agent", agent)]:
                (publish / f"{project}.deps.json").write_text(json.dumps({"libraries": {f"{project}/{version}": {}}}))
            if tag:
                distribution.os.environ.update(GITHUB_REF_TYPE="tag", GITHUB_REF_NAME=tag)
            return distribution.distribution_version(publish)

    def test_alpha_matches_both_applications(self):
        self.assertEqual(self.check_version("0.2.0-alpha.1", "0.2.0-alpha.1", "v0.2.0-alpha.1"), "0.2.0-alpha.1")

    def test_release_matches_both_applications(self):
        self.assertEqual(self.check_version("0.1.0-preview.15", "0.1.0-preview.15", "v0.1.0-preview.15"), "0.1.0-preview.15")

    def test_old_agent_cannot_enter_new_release(self):
        with self.assertRaisesRegex(RuntimeError, "versions differ"):
            self.check_version("0.1.0-preview.15", "0.1.0-preview.14", "v0.1.0-preview.15")

    def test_stale_build_cannot_enter_new_release(self):
        with self.assertRaisesRegex(RuntimeError, "release tag"):
            self.check_version("0.1.0-preview.14", "0.1.0-preview.14", "v0.1.0-preview.15")

    def test_local_build_remains_identifiable(self):
        self.assertEqual(self.check_version("0.1.0-dev", "0.1.0-dev"), "0.1.0-dev")
