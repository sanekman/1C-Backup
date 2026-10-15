"""Test the native LXC installer in disposable Debian 12, with an explicit systemctl recorder (not a systemd test)."""
import argparse,hashlib,json,re,subprocess,tempfile,uuid
from pathlib import Path

def main():
 repository=Path(__file__).resolve().parents[2]
 parser=argparse.ArgumentParser();parser.add_argument('--version',default=(repository/'deploy/storage/VERSION').read_text().strip());args=parser.parse_args()
 if not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+-alpha\.[0-9]+',args.version):raise ValueError('Invalid version')
 archive=repository/('artifacts/OneCBackup-Storage-'+args.version+'-linux-x64.tar.gz')
 image='onec-storage-debian12-'+uuid.uuid4().hex[:12]
 name='onec-storage-install-'+uuid.uuid4().hex[:12]
 with tempfile.TemporaryDirectory(prefix='onec-lxc-installer-') as directory:
  root=Path(directory)
  (root/'Dockerfile').write_text('FROM debian:bookworm-slim\nRUN apt-get update && apt-get install -y ca-certificates libicu72 libssl3 zlib1g libstdc++6 python3\n')
  subprocess.run(['docker','build','-t',image,str(root)],check=True,stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
  try:
   subprocess.run(['docker','run','-d','--name',name,'-v',str(archive.parent)+':/packages:ro',
    '-v',str(repository/'deploy/storage/install-lxc.sh')+':/setup/install-lxc.sh:ro',image,'sleep','infinity'],check=True,capture_output=True)
   command='''set -eu
mkdir -p /usr/local/bin
cat >/usr/local/bin/systemctl <<'SH'
#!/bin/sh
printf '%s\\n' "$*" >>/tmp/systemctl-calls.txt
SH
chmod +x /usr/local/bin/systemctl
bash /setup/install-lxc.sh --archive /packages/PACKAGE_ARCHIVE --storage /backups --endpoint wss://relay.example.com
bash /setup/install-lxc.sh --archive /packages/PACKAGE_ARCHIVE --storage /backups
test -x /opt/onec-backup-storage/current/OneCBackup.StorageBridge
test -f /etc/systemd/system/onec-backup-storage.service
test ! -f /etc/onec-backup-storage/agent.pfx
test ! -f /etc/onec-backup-storage/client-pairing.json
runuser -u onecbackup-storage -- test -r /etc/onec-backup-storage/gateway.pfx
runuser -u onecbackup-storage -- test -w /backups
python3 - <<'PY'
import json,glob,os
with open('/etc/onec-backup-storage/gateway.json') as file: value=json.load(file)
assert value['DataDirectory']=='/backups'
assert value['CertificateFile']=='/etc/onec-backup-storage/gateway.pfx'
assert value['AccessTokenFile']=='/etc/onec-backup-storage/access-token.txt'
assert len(glob.glob('/root/onec-backup-storage-pairing.*/client-pairing.json'))==1
assert os.stat('/etc/onec-backup-storage/gateway.json').st_mode & 0o777 == 0o600
PY
'''
   command=command.replace('PACKAGE_ARCHIVE',archive.name)
   result=subprocess.run(['docker','exec',name,'bash','-c',command],capture_output=True,text=True)
   if result.returncode:raise RuntimeError('Installer failed: '+result.stdout[-1500:]+result.stderr[-2000:])
   print(json.dumps({'Success':True,'Debian12PackageAndDependencies':True,'RepeatInstallationKeepsPairing':True,
    'ConfigurationPermissionsChecked':True,'PrivateAgentPairingKeptOutsideGatewayService':True,
    'NativeSystemdOrProxmoxTested':False}))
  finally:
   subprocess.run(['docker','rm','-f',name],capture_output=True)
   subprocess.run(['docker','image','rm',image],capture_output=True)

if __name__=='__main__':main()
