#!/usr/bin/env bash
set -euo pipefail
umask 077
# Run inside the target Debian 12 LXC. Git authentication must already be configured for a private repository.
usage() { echo 'Usage: install-from-github.sh --storage /mnt/nas/backups [--endpoint wss://relay.domain]' >&2; exit "${1:-2}"; }
require_value() { [[ $# -ge 2 && -n "$2" && "$2" != -* ]] || { echo "Option $1 requires a value." >&2; usage; }; }
installer_arguments=("$@"); storage_path=""
while (($#)); do
 case "$1" in
  --storage) require_value "$@"; storage_path="$2"; shift 2;;
  --endpoint) require_value "$@"; shift 2;;
  -h|--help) usage 0;;
  *) usage;;
 esac
done
[[ -n "$storage_path" ]] || usage
[[ $(id -u) == 0 ]] || { echo 'Run inside a Debian 12 LXC as root.' >&2; exit 1; }
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
version=$(cat "$root/VERSION")
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+-alpha\.[0-9]+$ ]] || { echo 'Invalid storage version.' >&2; exit 1; }
temporary=$(mktemp -d)
trap 'rm -rf "$temporary"' EXIT
git clone --depth 1 --single-branch --no-tags --branch "distribution/storage-linux-x64-$version" \
 https://github.com/sanekman/1C-Backup.git "$temporary/distribution"
archive="$temporary/distribution/storage/SPBackup-Storage-$version-linux-x64.tar.gz"
[[ -f "$archive" ]] || archive="$temporary/distribution/storage/OneCBackup-Storage-$version-linux-x64.tar.gz"
bash "$root/install-lxc.sh" --archive "$archive" "${installer_arguments[@]}"
