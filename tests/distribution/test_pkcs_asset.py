import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("distribution", Path(__file__).resolve().parents[2] / "tools/build_windows_distribution.py")
distribution = importlib.util.module_from_spec(spec)
spec.loader.exec_module(distribution)


class PackagedPkcsTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.publish = self.root / "publish"
        self.publish.mkdir()
        self.cache = self.root / "packages"
        self.package = "System.Security.Cryptography.Pkcs"
        self.asset = self.package + ".dll"
        restored = self.cache / self.package.lower() / "10.0.13" / "runtimes/win/lib/net10.0"
        restored.mkdir(parents=True)
        (restored / self.asset).write_bytes(b"patched PKCS fixture")
        (self.publish / self.asset).write_bytes(b"patched PKCS fixture")
        for project in ["OneCBackup.Desktop", "OneCBackup.Agent"]:
            self.deps(project)

    def deps(self, project, version="10.0.13", active=True):
        name = self.package + "/" + version
        runtime = {"runtime": {"runtimes/win/lib/net10.0/" + self.asset: {}}} if active else {}
        (self.publish / (project + ".deps.json")).write_text(json.dumps({
            "libraries": {name: {}}, "targets": {"net10.0/win-x64": {name: runtime}}}))

    def verify(self):
        distribution.verify_pkcs_asset(self.publish, [self.cache])

    def test_patched_runtime_asset_matches_restored_package(self):
        self.verify()

    def test_framework_fallback_is_rejected_even_when_nuget_restore_was_clean(self):
        (self.publish / "OneCBackup.Desktop.deps.json").write_text(json.dumps({"libraries": {}}))
        with self.assertRaisesRegex(RuntimeError, "runtime-pack"):
            self.verify()

    def test_stale_dll_cannot_be_shipped_under_patched_dependency_metadata(self):
        (self.publish / self.asset).write_bytes(b"old framework PKCS fixture")
        with self.assertRaisesRegex(RuntimeError, "differs"):
            self.verify()

    def test_agent_must_also_load_patched_runtime_asset(self):
        self.deps("OneCBackup.Agent", active=False)
        with self.assertRaisesRegex(RuntimeError, "does not load"):
            self.verify()

    def test_unpatched_major10_package_is_rejected(self):
        self.deps("OneCBackup.Desktop", version="10.0.12")
        with self.assertRaisesRegex(RuntimeError, "at least"):
            self.verify()
