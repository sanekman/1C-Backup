#!/usr/bin/env bash
set -euo pipefail
umask 077
archive=""; endpoint=""; data=""
usage() { echo 'Usage: install-lxc.sh --archive PACKAGE.tar.gz --storage /mnt/nas/backups [--endpoint wss://relay.domain]' >&2; exit "${1:-2}"; }
require_value() { [[ $# -ge 2 && -n "$2" && "$2" != -* ]] || { echo "Option $1 requires a value." >&2; usage; }; }
while (($#)); do
 case "$1" in
  --archive) require_value "$@"; archive="$2"; shift 2;;
  --endpoint) require_value "$@"; endpoint="$2"; shift 2;;
  --storage) require_value "$@"; data="$2"; shift 2;;
  -h|--help) usage 0;;
  *) usage;;
 esac
done
[[ -n "$archive" && -n "$data" ]] || usage
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
command -v flock >/dev/null || { echo 'Install util-linux (flock) before running the installer.' >&2; exit 1; }
install -d -m 700 /run/onec-backup-storage-installer
exec 9>/run/onec-backup-storage-installer/lock
flock -n 9 || { echo 'Another storage installation is already running.' >&2; exit 1; }
apt-get update
apt-get install -y ca-certificates libicu72 libssl3 zlib1g libstdc++6 python3
getent passwd onecbackup-storage >/dev/null || useradd --system --home-dir /var/lib/onec-backup-storage --shell /usr/sbin/nologin onecbackup-storage
install -d -m 755 /opt/onec-backup-storage
stage=$(mktemp -d /opt/onec-backup-storage/stage.XXXXXXXX)
current_link=/opt/onec-backup-storage/current
service_unit=/etc/systemd/system/onec-backup-storage.service
activation_started=false; had_current=false; had_unit=false
was_active=false; was_enabled=false
restart_attempted=false; enable_attempted=false; reload_attempted=false
# Copy into the destination filesystem before renaming: /opt and /etc may be separate mounts.
replace_path() {
 local saved="$1" destination="$2" temporary
 temporary=$(mktemp -d "$(dirname -- "$destination")/.onec-replace.XXXXXXXX") || return 1
 if cp -a -- "$saved" "$temporary/item" && mv -Tf -- "$temporary/item" "$destination"; then
  rmdir -- "$temporary"
 else
  rm -rf -- "$temporary"
  return 1
 fi
}
finish_installation() {
 local installation_status=$? rollback_failed=false
 trap - EXIT
 set +e
 if [[ "$installation_status" -ne 0 && "$activation_started" == true ]]; then
  if [[ "$restart_attempted" == true ]]; then
   systemctl stop onec-backup-storage.service || rollback_failed=true
  fi
  if [[ "$enable_attempted" == true && "$was_enabled" == false ]]; then
   systemctl disable onec-backup-storage.service || rollback_failed=true
  fi
  replace_path "$stage/gateway.before" /etc/onec-backup-storage/gateway.json || rollback_failed=true
  if [[ "$had_current" == true ]]; then
   replace_path "$stage/current.before" "$current_link" || rollback_failed=true
  else
   rm -f -- "$current_link" || rollback_failed=true
  fi
  if [[ "$had_unit" == true ]]; then
   replace_path "$stage/unit.before" "$service_unit" || rollback_failed=true
  else
   rm -f -- "$service_unit" || rollback_failed=true
  fi
  if [[ "$reload_attempted" == true ]]; then
   systemctl daemon-reload || rollback_failed=true
  fi
  if [[ "$restart_attempted" == true && "$was_active" == true ]]; then
   systemctl restart onec-backup-storage.service && systemctl is-active --quiet onec-backup-storage.service || rollback_failed=true
  fi
  if [[ "$rollback_failed" == true ]]; then
   echo "Activation and recovery failed. Recovery snapshots retained at $stage; check the storage service before retrying." >&2
   exit "$installation_status"
  fi
  echo 'Activation failed. Previous configuration, version link and service unit restored; backup data was kept.' >&2
 fi
 rm -rf -- "$stage"
 exit "$installation_status"
}
trap finish_installation EXIT
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
cp -a -- /etc/onec-backup-storage/gateway.json "$stage/gateway.before"
if [[ -e "$current_link" || -L "$current_link" ]]; then
 [[ -L "$current_link" ]] || { echo 'The current installation must be a symlink; nothing activated.' >&2; exit 1; }
 cp -a -- "$current_link" "$stage/current.before"; had_current=true
fi
if [[ -e "$service_unit" || -L "$service_unit" ]]; then
 cp -a -- "$service_unit" "$stage/unit.before"; had_unit=true
 if systemctl is-active --quiet onec-backup-storage.service; then was_active=true; fi
 if systemctl is-enabled --quiet onec-backup-storage.service; then was_enabled=true; fi
fi
activation_started=true
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
ln -s -- "$target" "$stage/current.next"
mv -Tf -- "$stage/current.next" "$current_link"
cat >"$stage/unit.next" <<'UNIT'
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
replace_path "$stage/unit.next" "$service_unit"
reload_attempted=true
systemctl daemon-reload
enable_attempted=true
systemctl enable onec-backup-storage.service
restart_attempted=true
systemctl restart onec-backup-storage.service
systemctl is-active --quiet onec-backup-storage.service
activation_started=false
echo 'Installed. Import the client pairing file printed during initialization into Windows; import hub.json into the public endpoint.'
echo 'Connection: journalctl -u onec-backup-storage.service -f'
