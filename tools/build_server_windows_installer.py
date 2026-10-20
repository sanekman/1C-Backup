"""Build the offline Windows server installer. PostgreSQL 17 binaries must be supplied by the build host."""
import argparse
import hashlib
import os
from pathlib import Path
import shutil
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=Path('artifacts/server/win-x64'))
    parser.add_argument('--postgres-bin', type=Path, default=Path(os.environ.get('PGBIN','')))
    parser.add_argument('--compiler', default=r'C:\Program Files (x86)\Inno Setup 6\ISCC.exe')
    parser.add_argument('--vc-redist',type=Path,default=Path('artifacts/server/vc_redist.x64.exe'))
    args = parser.parse_args()
    if not args.vc_redist.is_file():
        raise RuntimeError('The offline package requires the Microsoft VC++ x64 redistributable.')
    import tempfile
    with tempfile.TemporaryDirectory(prefix='onec-redist-verify-') as temporary:
        script=Path(temporary)/'verify.ps1'
        script.write_text("param([string]$Path)\n$s=Get-AuthenticodeSignature -LiteralPath $Path; if ($s.Status -ne 'Valid' -or $s.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') { throw 'Invalid Microsoft redistributable signature' }\n")
        subprocess.run(['powershell.exe','-NoProfile','-NonInteractive','-File',str(script),str(args.vc_redist.resolve())],check=True)

    source = args.source.resolve()
    pg_root = args.postgres_bin.resolve().parent
    postgres_version = subprocess.check_output([str(pg_root/'bin/postgres.exe'),'--version'],text=True).strip()
    if not postgres_version.startswith('postgres (PostgreSQL) 17.'):
        raise RuntimeError('The bundled database must be PostgreSQL 17.')
    target = source/'postgresql'
    if target.exists():
        shutil.rmtree(target)
    target.mkdir()
    for directory in ('bin','lib','share'):
        if not (pg_root/directory).is_dir():
            raise RuntimeError('Incomplete PostgreSQL runtime: '+directory)
        shutil.copytree(pg_root/directory,target/directory)
    # License distributed with PostgreSQL, also available in its share directory on EDB builds.
    license_file = next((p for p in (pg_root/'license.txt',pg_root/'COPYRIGHT',pg_root/'share/COPYRIGHT',Path(__file__).resolve().parents[1]/'deploy/server/windows/PostgreSQL-LICENSE.txt') if p.is_file()),None)
    if license_file is None:
        raise RuntimeError('PostgreSQL license is required in the offline package.')
    shutil.copyfile(license_file,target/'LICENSE.txt')
    (source/'POSTGRESQL-VERSION.txt').write_text(postgres_version+'\n')
    shutil.copyfile(args.vc_redist,source/'vc_redist.x64.exe')
    version = (source/'VERSION.txt').read_text().strip()
    import re
    if not re.fullmatch(r'\d+\.\d+\.\d+(?:-[a-zA-Z0-9.]+)?',version):
        raise RuntimeError('Invalid server version')
    output = source.parent
    installer = output/f'1C-Backup-Server-{version}-windows-x64-setup.exe'
    if installer.exists():
        raise FileExistsError('Refusing to overwrite an existing installer: '+str(installer))
    subprocess.run([args.compiler,'/Qp','/DSourceFolder='+str(source),'/DOutputFolder='+str(output),'/DAppVersion='+version,str(Path(__file__).with_name('server-windows-installer.iss'))],check=True)
    with installer.open('rb') as stream:
        if stream.read(2)!=b'MZ':
            raise RuntimeError('Installer is not a Windows executable')
        stream.seek(0)
        digest=hashlib.file_digest(stream,'sha256').hexdigest()
    installer.with_suffix('.exe.sha256').write_text(digest+'  '+installer.name+'\n')
    print('Offline server installer:',installer.name,'SHA-256:',digest)


if __name__=='__main__':
    main()
