#!/usr/bin/env python3
"""Create a pg_dump custom archive and atomically publish its immutable monitor report.

Connection settings/passwords come from PostgreSQL environment, pg_service.conf,
or .pgpass. The report contains no connection string or captured command output.
"""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import subprocess
import uuid


def run_backup(database, instance, backup_directory, report_directory,
               pg_dump="pg_dump", pg_restore="pg_restore", timeout_seconds=7200):
    if not database or "=" in database or any(ord(c) < 32 for c in database):
        raise ValueError("Use a database name; pass connection settings via PostgreSQL environment/service files.")
    if not instance.strip() or timeout_seconds < 1:
        raise ValueError("Instance label and positive timeout are required.")
    run_id = str(uuid.uuid4())
    backup_directory = Path(backup_directory).resolve()
    report_directory = Path(report_directory).resolve()
    backup_directory.mkdir(parents=True, exist_ok=True)
    report_directory.mkdir(parents=True, exist_ok=True)
    dump = backup_directory / (run_id + ".dump")
    partial = backup_directory / (run_id + ".dump.tmp")
    log = report_directory / (run_id + ".log")
    started = datetime.now(timezone.utc)
    outcome, evidence, error = "Failed", "PostgreSqlScriptFailure", None
    size, locations = None, []
    try:
        with log.open("xb") as output:
            result = subprocess.run(
                [pg_dump, "--format=custom", "--file=" + str(partial), "--dbname=" + database],
                stdout=subprocess.DEVNULL, stderr=output, timeout=timeout_seconds, check=False)
            if result.returncode != 0:
                raise RuntimeError("pg_dump failed; inspect the local log " + str(log))
            if not partial.exists() or partial.stat().st_size == 0:
                raise RuntimeError("pg_dump did not create a nonempty dump")
            result = subprocess.run([pg_restore, "--list", str(partial)],
                                    stdout=subprocess.DEVNULL, stderr=output, timeout=timeout_seconds, check=False)
            if result.returncode != 0:
                raise RuntimeError("pg_restore --list failed; inspect the local log " + str(log))
        if dump.exists():
            raise FileExistsError("A backup with this run ID already exists")
        partial.rename(dump)
        outcome, evidence = "Completed", "PgRestoreCatalogChecked"
        size, locations = dump.stat().st_size, [str(dump)]
    except (OSError, RuntimeError, subprocess.TimeoutExpired) as exc:
        # Do not export exception text from tools/connection settings or their stderr.
        error = "Backup or catalog check failed (" + type(exc).__name__ + "); inspect local log " + str(log)
    report = {
        "sourceKey": instance + ":" + run_id, "instance": instance, "databaseName": database,
        "backupType": "LogicalFull", "startedAt": started.isoformat().replace("+00:00", "Z"),
        "finishedAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "timeZoneId": "UTC", "outcome": outcome, "sizeBytes": size,
        "locations": locations, "evidence": evidence, "error": error,
    }
    temporary = report_directory / (run_id + ".json.tmp")
    with temporary.open("x", encoding="utf-8") as output:
        json.dump(report, output, ensure_ascii=False, indent=2)
        output.flush()
        os.fsync(output.fileno())
    temporary.rename(report_directory / (run_id + ".json"))
    return 0 if outcome == "Completed" else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", required=True)
    parser.add_argument("--instance", required=True, help="Stable cluster label, without credentials")
    parser.add_argument("--backup-directory", required=True)
    parser.add_argument("--report-directory", required=True)
    parser.add_argument("--pg-dump", default="pg_dump")
    parser.add_argument("--pg-restore", default="pg_restore")
    parser.add_argument("--timeout-seconds", type=int, default=7200)
    args = parser.parse_args()
    return run_backup(args.database, args.instance, args.backup_directory, args.report_directory,
                      args.pg_dump, args.pg_restore, args.timeout_seconds)


if __name__ == "__main__":
    raise SystemExit(main())
