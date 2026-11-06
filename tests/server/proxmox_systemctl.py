#!/usr/bin/env python3
"""Disposable Docker fixture only: record systemd calls and start real PG/server.

This is explicitly NOT a systemd implementation or a test on actual Proxmox.
"""
import os
from pathlib import Path
import signal
import subprocess
import sys
import time

args = sys.argv[1:]
with open('/tmp/systemctl-calls.txt', 'a') as log:
    log.write(' '.join(args) + '\n')
if args == ['restart', 'postgresql@17-main']:
    status = subprocess.run(['pg_ctlcluster', '17', 'main', 'status'], capture_output=True)
    command = 'restart' if status.returncode == 0 else 'start'
    subprocess.run(['pg_ctlcluster', '17', 'main', command], check=True)
elif args == ['enable', '--now', 'onec-backup-server']:
    failure = Path('/tmp/reject-first-server-start')
    if failure.exists():
        failure.unlink()
        sys.exit(1)
    with open('/tmp/native-server.log', 'a') as output:
        process = subprocess.Popen(['runuser', '-u', 'onec-backup-server', '--',
            '/opt/onec-backup-server/current/SPBackup.Server', '--Server:ConfigFile', '/etc/onec-backup-server/server.json'],
            stdout=output, stderr=subprocess.STDOUT, start_new_session=True)
    Path('/tmp/native-server.pid').write_text(str(process.pid))
elif args == ['stop', 'onec-backup-server']:
    pid = int(Path('/tmp/native-server.pid').read_text())
    os.killpg(pid, signal.SIGTERM)
    for _ in range(50):
        if not Path(f'/proc/{pid}/cmdline').exists() or not Path(f'/proc/{pid}/cmdline').read_bytes():
            break
        time.sleep(.1)
