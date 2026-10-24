#!/usr/bin/env python3
"""Publish a standalone management-server archive, keeping all credentials outside the package."""
import argparse
import hashlib
import json
import os
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
    subprocess.run([args.dotnet, "publish", "src/OneCBackup.Server", "-c", "Release", "-r", args.runtime,
                    "--self-contained", "true", "-p:RestoreLockedMode=true", "-o", str(output), "-p:Version=" + version], cwd=repository, check=True)
    (output / "VERSION.txt").write_text(version + "\n", encoding="utf-8")
    (output / "START-HERE.md").write_text((repository / "docs/server-start.md").read_text(encoding="utf-8"), encoding="utf-8")
    shutil.copy(repository / "deploy/server/server.env.example", output)
    if args.runtime == "win-x64":
        shutil.copytree(repository / "deploy/server/windows", output / "windows")
    if args.runtime == "linux-x64":
        shutil.copy(repository / "deploy/server/onec-backup-server.service", output)
    dependency = json.loads((output / "OneCBackup.Server.deps.json").read_text(encoding="utf-8"))
    if "OneCBackup.Server/" + version not in dependency["libraries"]:
        raise RuntimeError("Published server version mismatch")
    forbidden = {"connection-string.txt", "administrator-password.txt", "certificate-password.txt", "server.pfx", "postgres.env"}
    if any(path.name in forbidden or path.suffix in {".pfx", ".pem", ".jsonl"} for path in output.rglob("*")):
        raise RuntimeError("A credential or diagnostic file entered the distribution")
    archive = output.parent / ("1C-Backup-server-" + args.runtime + (".zip" if args.runtime == "win-x64" else ".tar.gz"))
    if args.runtime == "win-x64":
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as zip_file:
            for path in sorted(output.rglob("*")):
                if path.is_file():
                    zip_file.write(path, "1C-Backup-Server/" + path.relative_to(output).as_posix())
        with zipfile.ZipFile(archive) as zip_file:
            if zip_file.testzip() is not None:
                raise RuntimeError("Server archive CRC failed")
    else:
        with tarfile.open(archive, "w:gz") as tar:
            tar.add(output, arcname="1C-Backup-Server")
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    archive.with_name(archive.name + ".sha256").write_text(digest + "  " + archive.name + "\n")
    print("Server distribution:", archive.name, "SHA-256", digest)

if __name__ == "__main__":
    main()
