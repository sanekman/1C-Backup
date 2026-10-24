"""Publish the gateway/relay as a self-contained Linux x64 package for a Debian 12 Proxmox LXC."""
import argparse
import hashlib
import json
import re
from pathlib import Path
import subprocess
import tarfile
import tempfile

def main():
    repository = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser()
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--version', default=(repository / 'deploy/storage/VERSION').read_text().strip())
    args = parser.parse_args()
    if not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+-alpha\.[0-9]+', args.version):
        raise ValueError('Invalid storage version')
    artifacts = repository / 'artifacts'; artifacts.mkdir(exist_ok=True)
    archive = artifacts / ('OneCBackup-Storage-' + args.version + '-linux-x64.tar.gz')
    if archive.exists() or archive.with_suffix('.gz.sha256').exists():
        raise FileExistsError('Package already exists; no overwrite permitted')
    with tempfile.TemporaryDirectory(dir=artifacts, prefix='storage-publish-') as temporary:
        package = Path(temporary)
        subprocess.run([args.dotnet, 'publish', str(repository / 'src/OneCBackup.StorageBridge'), '-c', 'Release', '-r', 'linux-x64',
                        '--self-contained', 'true', '-o', str(package), '/p:Version=' + args.version, '/p:RuntimeFrameworkVersion=10.0.13'], check=True, cwd=repository)
        runtime = json.loads((package / 'OneCBackup.StorageBridge.runtimeconfig.json').read_text())
        frameworks = {item['name']: item['version'] for item in runtime['runtimeOptions']['includedFrameworks']}
        if frameworks != {'Microsoft.NETCore.App': '10.0.13', 'Microsoft.AspNetCore.App': '10.0.13'}:
            raise ValueError('Published package must include .NET and ASP.NET Core 10.0.13')
        (package / 'VERSION.txt').write_text(args.version + '\n')
        with tarfile.open(archive, 'x:gz') as output:
            for file in sorted(package.iterdir()): output.add(file, arcname='OneCBackup-Storage/' + file.name)
    digest = hashlib.file_digest(archive.open('rb'), 'sha256').hexdigest()
    archive.with_suffix('.gz.sha256').write_text(digest + '  ' + archive.name + '\n')
    print(archive.name + ' SHA256=' + digest)

if __name__ == '__main__': main()
