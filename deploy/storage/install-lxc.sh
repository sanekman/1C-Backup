#!/usr/bin/env bash
set -euo pipefail
umask 077
archive=""; endpoint=""; data=""
while (($#)); do
 case "$1" in
  --archive) archive="$2"; shift 2;;
  --endpoint) endpoint="$2"; shift 2;;
  --storage) data="$2"; shift 2;;
  *) echo 'Usage: install-lxc.sh --archive PACKAGE.tar.gz --storage /mnt/nas/backups [--endpoint wss://relay.domain]' >&2; exit 2;;
 esac
done
[[ $(id -u) == 0 ]] || { echo 'Run inside a Debian 12 LXC as root.' >&2; exit 1; }
source /etc/os-release
[[ "$ID" == debian && "$VERSION_ID" == 12 && $(uname -m) == x86_64 ]] || { echo 'This installer supports Debian 12 x64.' >&2; exit 1; }
[[ -f "$archive" && -f "$archive.sha256" && "$data" == /* && "$data" != / && ! -L "$data" ]] || { echo 'Provide archive, SHA256 file and a full storage path without symlinks.' >&2; exit 1; }
expected=$(cut -d ' ' -f 1 "$archive.sha256")
[[ "$expected" =~ ^[a-fA-F0-9]{64}$ ]] || { echo 'Invalid checksum file.' >&2; exit 1; }
actual=$(sha256sum "$archive"); actual=${actual%% *}
[[ "${actual,,}" == "${expected,,}" ]] || { echo 'Checksum mismatch.' >&2; exit 1; }
if [[ ! -f /etc/onec-backup-storage/gateway.json ]]; then
 [[ "$endpoint" == wss://* ]] || { echo 'Initial installation requires --endpoint wss://YOUR-DOMAIN.' >&2; exit 1; }
fi
apt-get update
apt-get install -y ca-certificates libicu72 libssl3 zlib1g libstdc++6 python3
getent passwd onecbackup-storage >/dev/null || useradd --system --home-dir /var/lib/onec-backup-storage --shell /usr/sbin/nologin onecbackup-storage
install -d -m 755 /opt/onec-backup-storage
stage=$(mktemp -d /opt/onec-backup-storage/stage.XXXXXXXX)
trap 'rm -rf "$stage"' EXIT
python3 - "$archive" "$stage" <<'PY'
import sys,tarfile,shutil,os
from pathlib import Path,PurePosixPath
root=Path(sys.argv[2]);seen=set();total=0
with tarfile.open(sys.argv[1]) as archive:
 for item in archive.getmembers():
  parts=PurePosixPath(item.name).parts
  if not parts or parts[0]!='OneCBackup-Storage' or '..' in parts or '\\' in item.name or item.name in seen or not (item.isfile() or item.isdir()): raise SystemExit('Invalid package member')
  seen.add(item.name);total+=item.size
  if len(seen)>4096 or item.size<0 or total>1024*1024*1024: raise SystemExit('Package limits exceeded')
  target=root.joinpath(*parts)
  if item.isdir(): target.mkdir(parents=True,exist_ok=True)
  else:
   target.parent.mkdir(parents=True,exist_ok=True)
   with archive.extractfile(item) as source,target.open('xb') as output:shutil.copyfileobj(source,output)
   os.chmod(target,item.mode & 0o755)
PY
package="$stage/OneCBackup-Storage"
[[ -x "$package/OneCBackup.StorageBridge" && -f "$package/VERSION.txt" ]] || { echo 'Invalid package.' >&2; exit 1; }
version=$(cat "$package/VERSION.txt")
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+-alpha\.[0-9]+$ ]] || { echo 'Invalid version.' >&2; exit 1; }
target="/opt/onec-backup-storage/$version"
if [[ -e "$target" ]]; then
 [[ -f "$target/.package-sha256" && $(cat "$target/.package-sha256") == "$actual" ]] || { echo 'Existing version differs; no files overwritten.' >&2; exit 1; }
else
 mv "$package" "$target"; chmod -R a+rX "$target"; echo "$actual" >"$target/.package-sha256"
fi
if [[ ! -f /etc/onec-backup-storage/gateway.json ]]; then
 "$target/OneCBackup.StorageBridge" initialize /etc/onec-backup-storage "$endpoint"
 pairing_output=$(mktemp -d /root/onec-backup-storage-pairing.XXXXXXXX)
 mv /etc/onec-backup-storage/client-pairing.json "$pairing_output/client-pairing.json"
 rm /etc/onec-backup-storage/agent.pfx
 echo "Client pairing file: $pairing_output/client-pairing.json"
fi
chown -R onecbackup-storage:onecbackup-storage /etc/onec-backup-storage
chmod 700 /etc/onec-backup-storage; chmod 600 /etc/onec-backup-storage/*
if [[ ! -d "$data" ]]; then install -d -m 700 -o onecbackup-storage -g onecbackup-storage "$data"; fi
runuser -u onecbackup-storage -- test -w "$data" || { echo 'Storage is not writable by onecbackup-storage. Set NAS/mount permissions; existing data ownership was not changed.' >&2; exit 1; }
python3 - "$data" <<'PY'
import json,sys,os
path='/etc/onec-backup-storage/gateway.json'
with open(path) as file: options=json.load(file)
options['DataDirectory']=sys.argv[1]
for name,file in [('AccessTokenFile','access-token.txt'),('CertificateFile','gateway.pfx'),('CertificatePasswordFile','certificate-password.txt'),('PeerCertificateFile','agent.pem')]:
 options[name]='/etc/onec-backup-storage/'+file
with open(path+'.tmp','w') as file: json.dump(options,file,indent=2)
os.replace(path+'.tmp',path)
PY
chown onecbackup-storage:onecbackup-storage /etc/onec-backup-storage/gateway.json
chmod 600 /etc/onec-backup-storage/gateway.json
ln -sfn "$target" /opt/onec-backup-storage/current
cat >/etc/systemd/system/onec-backup-storage.service <<'UNIT'
[Unit]
Description=1C-Backup storage gateway
Wants=network-online.target
After=network-online.target
[Service]
User=onecbackup-storage
Group=onecbackup-storage
ExecStart=/opt/onec-backup-storage/current/OneCBackup.StorageBridge gateway /etc/onec-backup-storage/gateway.json
Restart=always
RestartSec=5
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=full
UMask=0077
[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload
systemctl enable onec-backup-storage.service
systemctl restart onec-backup-storage.service
echo 'Installed. Import the client pairing file printed during initialization into Windows; import hub.json into the public endpoint.'
echo 'Connection: journalctl -u onec-backup-storage.service -f'
