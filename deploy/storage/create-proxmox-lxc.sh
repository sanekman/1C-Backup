#!/usr/bin/env bash
set -euo pipefail
id=""; data=""; template=""; rootfs="local-lvm"; bridge="vmbr0"
while (($#)); do
 case "$1" in
  --id) id="$2"; shift 2;; --storage-path) data="$2"; shift 2;; --template) template="$2"; shift 2;;
  --rootfs-pool) rootfs="$2"; shift 2;; --bridge) bridge="$2"; shift 2;;
  *) echo 'Usage: create-proxmox-lxc.sh --id 120 --storage-path /mnt/nas/backups --template local:vztmpl/debian-12-standard_....tar.zst [--rootfs-pool local-lvm] [--bridge vmbr0]' >&2; exit 2;;
 esac
done
[[ $(id -u) == 0 && "$id" =~ ^[1-9][0-9]{2,8}$ && "$data" == /* && -d "$data" && ! -L "$data" && "$data" != *','* && "$template" == *'debian-12'* ]] || { echo 'Specify an unused CT ID, existing storage folder and Debian 12 template.' >&2; exit 1; }
command -v pct >/dev/null || { echo 'Run on the Proxmox host.' >&2; exit 1; }
[[ ! -f "/etc/pve/lxc/$id.conf" && ! -f "/etc/pve/qemu-server/$id.conf" ]] || { echo 'VM/CT ID already exists; nothing changed.' >&2; exit 1; }
pct create "$id" "$template" --hostname onec-storage --unprivileged 1 --cores 1 --memory 512 --swap 256 \
 --rootfs "$rootfs:8" --net0 "name=eth0,bridge=$bridge,ip=dhcp" --mp0 "$data,mp=/backups"
pct start "$id"
echo "CT $id created. Enter with: pct enter $id"
echo 'Install the gateway inside the CT using install-from-github.sh. Configure NAS permissions for the mapped service UID; existing host data was not chowned.'
