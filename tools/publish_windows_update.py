"""Publish an immutable update distribution without changing the checkout or its Git index."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import zipfile

FILES = ["1C-Backup-windows-x64.zip", "1C-Backup-windows-x64.zip.sha256",
         "1C-Backup-windows-x64-setup.exe", "1C-Backup-windows-x64-setup.exe.sha256", "update-manifest.json"]


def verify_artifacts(artifacts):
    manifest = json.loads((artifacts / "update-manifest.json").read_text(encoding="utf-8"))
    version = manifest["Version"]
    if (manifest["SchemaVersion"] != 1 or manifest["Platform"] != "win-x64"
            or manifest["InstallerKind"] not in ["nsis", "inno"]
            or manifest["InstallerFile"] != "1C-Backup-windows-x64-setup.exe"
            or not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.]+)?", version)):
        raise ValueError("Invalid update manifest")
    for name in [FILES[0], FILES[2]]:
        file = artifacts / name
        with file.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        expected = (artifacts / (name + ".sha256")).read_text().split()[0]
        if digest != expected:
            raise ValueError("Checksum mismatch: " + name)
        if name == FILES[2] and (digest != manifest["Sha256"] or file.stat().st_size != manifest["Size"]):
            raise ValueError("Installer does not match update manifest")
    with (artifacts / FILES[2]).open("rb") as stream:
        if stream.read(2) != b"MZ":
            raise ValueError("Installer is not a Windows executable")
    with zipfile.ZipFile(artifacts / FILES[0]) as archive:
        if archive.testzip() is not None or archive.read("1C-Backup/VERSION.txt").decode().strip() != version:
            raise ValueError("Distribution is corrupt or its version differs")
    return version


def publish(root, artifacts, branch=None, notes=None):
    version = verify_artifacts(artifacts)
    branch = branch or "distribution/windows-x64-" + version
    if not re.fullmatch(r"distribution/windows-x64-[A-Za-z0-9.\-]+", branch):
        raise ValueError("Only dedicated distribution branches can be published")

    def git(*args, input=None, env=None):
        return subprocess.check_output(["git", *args], cwd=root, input=input, env=env).decode().strip()

    git("check-ref-format", "refs/heads/" + branch)
    if git("ls-remote", "origin", "refs/heads/" + branch):
        raise ValueError("Distribution branch already exists; publish a new version")
    with tempfile.TemporaryDirectory(prefix="onec-update-git-") as temporary:
        env = dict(os.environ, GIT_INDEX_FILE=str(Path(temporary) / "index"))
        git("read-tree", "--empty", env=env)
        files = FILES + [name for name in ["installer-payload-result.json", "icon-verification-result.json"] if (artifacts / name).is_file()]
        for name in files:
            blob = git("hash-object", "-w", str(artifacts / name))
            git("update-index", "--add", "--cacheinfo", "100644", blob, "windows/" + name, env=env)
        tree = git("write-tree", env=env)
        commit = git("-c", "user.name=1C-Backup Release", "-c", "user.email=release@users.noreply.github.com",
                     "commit-tree", tree, input=("Publish Windows update " + version + "\n").encode())
        subprocess.run(["git", "push", "origin", commit + ":refs/heads/" + branch], cwd=root, check=True)
    (artifacts / "distribution-commit.txt").write_text(commit + "\n")
    body = notes.read_text(encoding="utf-8") if notes else "1C-Backup " + version
    base = "https://github.com/sanekman/1C-Backup/blob/" + branch + "/windows/"
    body += "\n\n[Скачать установщик](" + base + FILES[2] + "?raw=true)\n\n[Скачать ZIP](" + base + FILES[0] + "?raw=true)\n"
    body += "\n<!-- onec-update-manifest: " + branch + " -->\n"
    (artifacts / "release-notes-with-updates.md").write_text(body, encoding="utf-8")
    print("Update distribution: " + branch + "; commit: " + commit)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts", type=Path, default=Path("artifacts"))
    parser.add_argument("--branch")
    parser.add_argument("--notes", type=Path)
    args = parser.parse_args()
    publish(Path(__file__).resolve().parents[1], args.artifacts.resolve(), args.branch, args.notes)
