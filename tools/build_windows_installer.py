"""Compile a per-user installer from the already validated distribution ZIP."""

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import tempfile
import zipfile


def build(archive_path, compiler, kind="inno"):
    archive_path = Path(archive_path).resolve()
    artifacts = archive_path.parent
    expected = archive_path.with_suffix('.zip.sha256').read_text(encoding='ascii').split()[0]
    with archive_path.open('rb') as stream:
        if hashlib.file_digest(stream, 'sha256').hexdigest() != expected:
            raise RuntimeError('Distribution ZIP checksum mismatch')
    with tempfile.TemporaryDirectory(prefix='installer-input-', dir=artifacts) as temporary:
        source = Path(temporary)
        with zipfile.ZipFile(archive_path) as archive:
            if archive.testzip() is not None:
                raise RuntimeError('Corrupt distribution ZIP')
            for member in archive.infolist():
                parts = PurePosixPath(member.filename).parts
                if len(parts) < 2 or parts[0] != '1C-Backup' or '..' in parts or '\\' in member.filename or ':' in member.filename:
                    raise RuntimeError('Unexpected distribution path')
                target = source.joinpath(*parts[1:])
                if member.is_dir():
                    target.mkdir(parents=True, exist_ok=True)
                else:
                    target.parent.mkdir(parents=True, exist_ok=True)
                    with target.open('xb') as output:
                        with archive.open(member) as input_stream:
                            shutil.copyfileobj(input_stream, output)
        version = (source / 'VERSION.txt').read_text(encoding='utf-8').strip()
        if not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.]+)?', version):
            raise RuntimeError('Invalid distribution version')
        if kind == "inno":
            subprocess.run([str(compiler), '/Qp', '/DSourceFolder=' + str(source),
                            '/DOutputFolder=' + str(artifacts), '/DAppVersion=' + version,
                            str(Path(__file__).with_name('windows-installer.iss'))], check=True)
        elif kind == "nsis":
            def quote(value):
                return str(value).replace('$', '$$').replace('"', '$' + chr(92) + '"')
            # Keep generated uninstall metadata outside the input distribution.
            with tempfile.TemporaryDirectory(prefix='nsis-manifest-', dir=artifacts) as metadata:
                manifest = Path(metadata) / 'uninstall-files.nsh'
                lines = ['Delete "$INSTDIR' + chr(92) + quote(str(file.relative_to(source)).replace('/', chr(92))) + '"'
                         for file in sorted(source.rglob('*')) if file.is_file()]
                directories = sorted((file for file in source.rglob('*') if file.is_dir()),
                                     key=lambda file: len(file.parts), reverse=True)
                lines += ['RMDir "$INSTDIR' + chr(92) + quote(str(directory.relative_to(source)).replace('/', chr(92))) + '"'
                          for directory in directories]
                manifest.write_text('\n'.join(lines) + '\n', encoding='utf-8')
                subprocess.run([str(compiler), '-V2', '-DSourceFolder=' + str(source),
                                '-DOutputFolder=' + str(artifacts), '-DAppVersion=' + version,
                                '-DUninstallFiles=' + str(manifest),
                                str(Path(__file__).with_name('windows-installer.nsi'))], check=True)
        else:
            raise ValueError('Unknown installer compiler')
    installer = artifacts / '1C-Backup-windows-x64-setup.exe'
    with installer.open('rb') as stream:
        if stream.read(2) != b'MZ':
            raise RuntimeError('Installer is not a Windows executable')
        stream.seek(0)
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    installer.with_suffix('.exe.sha256').write_text(f'{digest}  {installer.name}\n', encoding='ascii')
    manifest = {"SchemaVersion": 1, "Version": version, "Platform": "win-x64", "InstallerKind": kind,
                "InstallerFile": installer.name, "Size": installer.stat().st_size, "Sha256": digest}
    (artifacts / "update-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f'Installer: {installer.name}; version: {version}; SHA256: {digest}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--zip', type=Path, default=Path('artifacts/1C-Backup-windows-x64.zip'))
    parser.add_argument('--kind', choices=['inno', 'nsis'], default='inno')
    parser.add_argument('--compiler', type=Path)
    args = parser.parse_args()
    compiler = args.compiler or (Path("makensis") if args.kind == "nsis" else Path(os.environ.get("ProgramFiles(x86)", "C:/Program Files (x86)")) / "Inno Setup 6/ISCC.exe")
    build(args.zip, compiler, args.kind)
