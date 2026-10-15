#!/usr/bin/env bash
set -euo pipefail
umask 077
# Run inside the target Debian 12 LXC. Git authentication must already be configured for a private repository.
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
version=$(cat "$root/VERSION")
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+-alpha\.[0-9]+$ ]] || { echo 'Invalid storage version.' >&2; exit 1; }
temporary=$(mktemp -d)
trap 'rm -rf "$temporary"' EXIT
git clone --depth 1 --single-branch --no-tags --branch "distribution/storage-linux-x64-$version" \
 https://github.com/sanekman/1C-Backup.git "$temporary/distribution"
bash "$root/install-lxc.sh" --archive "$temporary/distribution/storage/OneCBackup-Storage-$version-linux-x64.tar.gz" "$@"
