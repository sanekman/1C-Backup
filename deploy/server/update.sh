#!/bin/sh
set -eu
exec python3 "$(dirname "$0")/../../tools/update_server_container.py" "$@"
