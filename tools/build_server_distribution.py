#!/usr/bin/env python3
"""Publish a standalone management-server archive, keeping all credentials outside the package."""
import argparse
import hashlib
import json
import os
import re
from pathlib import Path
import shutil
import subprocess
import tarfile
import zipfile

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--runtime", choices=["win-x64", "linux-x64"], required=True)
    args = parser.parse_args()
    repository = Path(__file__).resolve().parents[1]
    output = repository / "artifacts/server" / args.runtime
    if output.exists():
        shutil.rmtree(output)
    version = "0.1.0-dev"
    reference = os.environ.get("GITHUB_REF_NAME", "")
    if os.environ.get("GITHUB_REF_TYPE") == "tag" and reference.startswith("server-v"):
        version = reference.removeprefix("server-v")
    subprocess.run([args.dotnet, "publish", "src/SPBackup.Server", "-c", "Release", "-r", args.runtime,
                    "--self-contained", "true", "-p:RestoreLockedMode=true", "-o", str(output), "-p:Version=" + version], cwd=repository, check=True)
    from legacy_apphosts import copy_legacy_apphosts
    copy_legacy_apphosts(output, ["Server"], windows=args.runtime == "win-x64")
    (output / "VERSION.txt").write_text(version + "\n", encoding="utf-8")
    shutil.copytree(repository / "docs", output / "docs")
    start = (repository / "docs/server-start.md").read_text(encoding="utf-8")
    start = re.sub(r'\]\(([\w-]+\.md(?:#[^)]*)?)\)', r'](docs/\1)', start)
    (output / "START-HERE.md").write_text(start, encoding="utf-8")
    shutil.copy(repository / "deploy/server/server.env.example", output)
    if args.runtime == "win-x64":
        shutil.copytree(repository / "deploy/server/windows", output / "windows")
    if args.runtime == "linux-x64":
        shutil.copy(repository / "deploy/server/onec-backup-server.service", output)
        installer = (repository / "deploy/server/create-proxmox-lxc.py").read_text(encoding="utf-8")
        if re.fullmatch(r'0\.1\.0-preview\.[1-9][0-9]*', version):
            installer = re.sub(r'^VERSION = .*$', 'VERSION = ' + repr(version), installer, count=1, flags=re.MULTILINE)
        (output / "create-proxmox-lxc.py").write_text(installer, encoding="utf-8")
    dependency = json.loads((output / "SPBackup.Server.deps.json").read_text(encoding="utf-8"))
    if "SPBackup.Server/" + version not in dependency["libraries"]:
        raise RuntimeError("Published server version mismatch")
    forbidden = {"connection-string.txt", "administrator-password.txt", "certificate-password.txt", "server.pfx", "postgres.env"}
    if any(path.name in forbidden or path.suffix in {".pfx", ".pem", ".jsonl"} for path in output.rglob("*")):
        raise RuntimeError("A credential or diagnostic file entered the distribution")
    archive = output.parent / ("SP-Backup-server-" + args.runtime + (".zip" if args.runtime == "win-x64" else ".tar.gz"))
    if args.runtime == "win-x64":
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as zip_file:
            for path in sorted(output.rglob("*")):
                if path.is_file():
                    zip_file.write(path, "SP-Backup-Server/" + path.relative_to(output).as_posix())
        with zipfile.ZipFile(archive) as zip_file:
            if zip_file.testzip() is not None:
                raise RuntimeError("Server archive CRC failed")
    else:
        with tarfile.open(archive, "w:gz") as tar:
            tar.add(output, arcname="SP-Backup-Server")
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    archive.with_name(archive.name + ".sha256").write_text(digest + "  " + archive.name + "\n")
    print("Server distribution:", archive.name, "SHA-256", digest)

if __name__ == "__main__":
    main()
