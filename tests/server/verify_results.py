"""Ensure required integration actually ran; an all-skipped suite must not pass CI."""
import argparse
from pathlib import Path
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--runtime", choices=["linux-x64", "win-x64"], required=True)
args = parser.parse_args()
platform = "windows" if args.runtime == "win-x64" else "linux"
root = Path(__file__).resolve().parents[2] / "artifacts/server-tests"
for name, count in [(f"server-{platform}.trx", 74), (f"server-package-{platform}.trx", 1)]:
    tree = ET.parse(root / name)
    counters = tree.find(".//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}Counters")
    if counters is None or int(counters.attrib.get("total", 0)) != count or int(counters.attrib.get("passed", 0)) != count or int(counters.attrib.get("failed", 0)) != 0:
        raise RuntimeError(f"{name}: expected {count} actual passing tests")
    print(name, count, "passed; no required checks skipped")
