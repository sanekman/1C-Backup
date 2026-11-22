"""Verify CI distributions and make a manifest and isolated LXC test kit."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import tarfile
import zipfile


def digest(path):
    with path.open('rb') as source:
        return hashlib.file_digest(source, 'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--artifacts', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--version', required=True)
    parser.add_argument('--source', required=True)
    parser.add_argument('--run', type=int, required=True)
    args = parser.parse_args()
    if not re.fullmatch(r'0\.1\.0-preview\.[1-9][0-9]*', args.version) or not re.fullmatch(r'[0-9a-f]{40}', args.source):
        parser.error('Expected a specific preview version and source commit')

    version = args.version
    archive = 'SP-Backup-server-linux-x64.tar.gz'
    windows_zip = 'SP-Backup-server-win-x64.zip'
    installer = f'SP-Backup-Server-{version}-windows-x64-setup.exe'
    assets = []
    for name, platform in ((installer, 'windows'), (windows_zip, 'windows'), (archive, 'linux')):
        matches = list((args.artifacts / platform).rglob(name))
        if len(matches) != 1:
            raise ValueError(f'Expected exactly one candidate distribution {name}')
        path = matches[0]
        sha256 = digest(path)
        if path.with_name(name + '.sha256').read_text().split() != [sha256, name]:
            raise ValueError(f'Invalid candidate checksum for {name}')
        if name == archive:
            linux_package = path
            with tarfile.open(path, 'r:gz') as package:
                version_file = package.extractfile('SP-Backup-Server/VERSION.txt').read()
        elif name == windows_zip:
            with zipfile.ZipFile(path) as package:
                version_file = package.read('SP-Backup-Server/VERSION.txt')
        else:
            version_file = version.encode()
        if version_file.decode('utf-8').strip() != version:
            raise ValueError(f'Unexpected packaged version in {name}')
        assets.append(dict(name=name, bytes=path.stat().st_size, sha256=sha256,
                           url=f'https://github.com/sanekman/1C-Backup/releases/download/server-v{version}/{name}'))

    repository = Path(__file__).resolve().parents[1]
    schema = int(re.search(r'const int SchemaVersion = ([0-9]+);',
                           (repository / 'src/SPBackup.Server/ServerStore.cs').read_text())[1])
    manifest = dict(version=version, runtime='10.0.13', schema=schema, sourceCommit=args.source,
                    candidateRun=args.run, published=False, assets=assets)
    args.output.mkdir(parents=True)
    (args.output / 'server-release.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    shutil.copy(linux_package, args.output)
    shutil.copy(repository / 'deploy/server/create-proxmox-lxc.py', args.output)
    shutil.copy(repository / 'tests/server/lxc_recovery_acceptance.py', args.output)
    sums = ''.join(f'{digest(path)}  {path.name}\n' for path in sorted(args.output.iterdir()))
    (args.output / 'SHA256SUMS').write_text(sums, encoding='utf-8')
    for asset in assets:
        print(asset['sha256'], asset['name'])


if __name__ == '__main__':
    main()
