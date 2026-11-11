"""Ensure required integration actually ran; an all-skipped suite must not pass CI."""
import argparse
import json
from pathlib import Path
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--runtime", choices=["linux-x64", "win-x64"], required=True)
args = parser.parse_args()
platform = "windows" if args.runtime == "win-x64" else "linux"
root = Path(__file__).resolve().parents[2] / "artifacts/server-tests"
for name, count in [(f"server-{platform}.trx", 168), (f"server-package-{platform}.trx", 1)]:
    tree = ET.parse(root / name)
    counters = tree.find(".//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}Counters")
    if counters is None or int(counters.attrib.get("total", 0)) != count or int(counters.attrib.get("passed", 0)) != count or int(counters.attrib.get("failed", 0)) != 0:
        raise RuntimeError(f"{name}: expected {count} actual passing tests")
    print(name, count, "passed; no required checks skipped")

if platform == "windows":
    reports = {
        "windows-installer-result.json": {"installed", "administratorLogin", "reinstallPreservesData", "coldSnapshotRoundTrip"},
        "windows-snapshot-result.json": {"coldBackup", "checksumVerification", "startupFailureRollback", "restoredDatabase"},
        "windows-upgrade-result.json": {"baselinePreview8", "schema5To6", "fullProgramInventory", "fullProgramAndDataRollback", "failedRollbackRestoresBothTrees", "migrationFailureRecovery", "stoppedServicesDiagnosedReadOnly"},
    }
    for name, required in reports.items():
        report = json.loads((root / name).read_text(encoding="utf-8-sig"))
        if not required <= report.keys() or not all(value is True for value in report.values()):
            raise RuntimeError(f"{name}: required native Windows checks did not pass")
        print(name, len(report), "native checks passed")
