import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("pg_report", Path(__file__).resolve().parents[2] / "tools" / "postgresql_backup_report.py")
pg_report = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pg_report)


class BackupReportTests(unittest.TestCase):
    def test_success_requires_dump_and_catalog_check(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            calls = []

            def run(arguments, **kwargs):
                calls.append(arguments)
                if arguments[0] == "pg_dump":
                    Path(next(arg.removeprefix("--file=") for arg in arguments if arg.startswith("--file="))).write_bytes(b"dump fixture")
                return subprocess.CompletedProcess(arguments, 0)

            with patch.object(pg_report.subprocess, "run", side_effect=run):
                self.assertEqual(0, pg_report.run_backup("database with spaces", "cluster", root / "copies", root / "reports"))
            report = json.loads(next((root / "reports").glob("*.json")).read_text())
            self.assertEqual("Completed", report["outcome"])
            self.assertEqual("PgRestoreCatalogChecked", report["evidence"])
            self.assertEqual(2, len(calls))
            self.assertIn("--dbname=database with spaces", calls[0])
            self.assertTrue(Path(report["locations"][0]).exists())
            self.assertEqual("UTC", report["timeZoneId"])
            self.assertEqual([], list((root / "reports").glob("*.tmp")))

    def test_failed_dump_is_reported_without_claiming_a_copy(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with patch.object(pg_report.subprocess, "run", return_value=subprocess.CompletedProcess([], 1)) as run:
                self.assertEqual(1, pg_report.run_backup("db", "cluster", root / "copies", root / "reports"))
                self.assertEqual(1, run.call_count)
            report = json.loads(next((root / "reports").glob("*.json")).read_text())
            self.assertEqual("Failed", report["outcome"])
            self.assertEqual([], report["locations"])
            self.assertIsNone(report["sizeBytes"])

    def test_connection_string_is_not_accepted_as_database_name(self):
        with self.assertRaises(ValueError):
            pg_report.run_backup("host=server password=secret", "cluster", "unused", "unused")


if __name__ == "__main__":
    unittest.main()
