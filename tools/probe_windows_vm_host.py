"""Probe a disposable Linux runner before provisioning an isolated Windows VM."""
import argparse
import fcntl
import json
import os
from pathlib import Path
import shutil
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument("--output", type=Path, required=True)
args = parser.parse_args()
result = {"source": os.environ.get("GITHUB_SHA"), "kvm_present": Path("/dev/kvm").exists(),
          "disk_free_bytes": shutil.disk_usage(".").free, "kvm_usable": False}
try:
    with open("/proc/meminfo", encoding="ascii") as stream:
        result["memory_available_bytes"] = int(next(line.split()[1] for line in stream if line.startswith("MemAvailable:"))) * 1024
    with open("/dev/kvm", "r+b", buffering=0) as device:
        result["kvm_api"] = fcntl.ioctl(device.fileno(), 0xAE00, 0)
        vm = fcntl.ioctl(device.fileno(), 0xAE01, 0)
        os.close(vm)
        result["kvm_usable"] = result["kvm_api"] == 12
except (OSError, StopIteration, ValueError) as error:
    result["error"] = str(error)
result["success"] = result["kvm_usable"] and result.get("memory_available_bytes", 0) >= 5 * 1024**3
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(result, indent=2), encoding="utf-8")
print(json.dumps(result))
raise SystemExit(0 if result["success"] else 1)
