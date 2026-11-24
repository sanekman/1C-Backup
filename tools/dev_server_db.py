#!/usr/bin/env python3
"""Owns only a labelled loopback PostgreSQL development container; never prints its credentials."""
import argparse
import json
import os
from pathlib import Path
import secrets
import subprocess
import time

LABEL = "onec-backup-purpose"

def docker(*args, check=True):
    return subprocess.run(["docker", *args], check=check, capture_output=True, text=True)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["start", "stop"])
    parser.add_argument("--state-dir", type=Path, default=Path(__file__).resolve().parents[1] / "artifacts/server-dev")
    parser.add_argument("--name", default="onec-backup-server-dev")
    parser.add_argument("--port", type=int, default=15432)
    args = parser.parse_args()
    state = args.state_dir.resolve()
    existing = docker("inspect", args.name, check=False)
    info = json.loads(existing.stdout)[0] if existing.returncode == 0 else None
    if info and info["Config"].get("Labels", {}).get(LABEL) != "development":
        raise RuntimeError("Container belongs to another workflow; choose a different --name.")
    if args.action == "stop":
        if info:
            docker("stop", args.name)
            docker("rm", args.name)
        print("Development PostgreSQL stopped; local credential files retained.")
        return
    if not 1024 <= args.port <= 65535:
        raise ValueError("Port must be between 1024 and 65535.")
    state.mkdir(parents=True, exist_ok=True)
    if os.name != "nt":
        os.chmod(state, 0o700)
    env_file, connection_file = state / "postgres.env", state / "connection-string.txt"
    if info and not connection_file.exists():
        raise RuntimeError("Existing container has no matching local state; do not overwrite its credentials.")
    if not info:
        password = secrets.token_urlsafe(32)
        for path, text in [
            (env_file, "POSTGRES_USER=onecbackup\nPOSTGRES_DB=onecbackup\nPOSTGRES_PASSWORD=" + password + "\n"),
            (connection_file, f"Host=127.0.0.1;Port={args.port};Database=onecbackup;Username=onecbackup;Password={password}")
        ]:
            descriptor = os.open(path, os.O_CREAT | os.O_TRUNC | os.O_WRONLY, 0o600)
            with os.fdopen(descriptor, "w") as stream:
                stream.write(text)
        result = docker("run", "-d", "--name", args.name, "--label", LABEL + "=development", "--env-file", str(env_file),
                        "--publish", f"127.0.0.1:{args.port}:5432", "postgres:17-alpine", check=False)
        if result.returncode:
            raise RuntimeError("Docker could not start development PostgreSQL; check image availability and port ownership.")
    elif not info["State"]["Running"]:
        docker("start", args.name)
    expected = str(args.port)
    current = json.loads(docker("inspect", args.name).stdout)[0]
    bindings = current["NetworkSettings"]["Ports"].get("5432/tcp", [])
    if not any(item["HostIp"] == "127.0.0.1" and item["HostPort"] == expected for item in bindings):
        raise RuntimeError("Container port differs from requested loopback port.")
    deadline = time.monotonic() + 40
    while time.monotonic() < deadline:
        if docker("exec", args.name, "pg_isready", "-U", "onecbackup", "-d", "onecbackup", check=False).returncode == 0:
            print("Development PostgreSQL ready on loopback. Connection file:", connection_file)
            return
        time.sleep(0.5)
    raise RuntimeError("Development PostgreSQL did not become ready.")

if __name__ == "__main__":
    main()
