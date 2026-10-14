"""Publish the gateway/relay as a self-contained Linux x64 package for a Debian 12 Proxmox LXC."""
import argparse
import hashlib
from pathlib import Path
import subprocess
import tarfile
import tempfile

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--version', default='0.1.0-alpha.1')
    args = parser.parse_args()
    repository = Path(__file__).resolve().parents[1]
    artifacts = repository / 'artifacts'; artifacts.mkdir(exist_ok=True)
    archive = artifacts / ('OneCBackup-Storage-' + args.version + '-linux-x64.tar.gz')
    with tempfile.TemporaryDirectory(dir=artifacts, prefix='storage-publish-') as temporary:
        package = Path(temporary)
        subprocess.run([args.dotnet, 'publish', str(repository / 'src/OneCBackup.StorageBridge'), '-c', 'Release', '-r', 'linux-x64',
                        '--self-contained', 'true', '-o', str(package), '/p:Version=' + args.version], check=True, cwd=repository)
        (package / 'VERSION.txt').write_text(args.version + '\n')
        with tarfile.open(archive, 'w:gz') as output:
            for file in sorted(package.iterdir()): output.add(file, arcname='OneCBackup-Storage/' + file.name)
    digest = hashlib.file_digest(archive.open('rb'), 'sha256').hexdigest()
    archive.with_suffix('.gz.sha256').write_text(digest + '  ' + archive.name + '\n')
    print(archive.name + ' SHA256=' + digest)

if __name__ == '__main__': main()
